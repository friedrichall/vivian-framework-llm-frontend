#if UNITY_EDITOR
using System;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;
using Vivian.Backend.Client;
using Vivian.Backend.Dtos;

public sealed partial class VivianBackendWindow
{
    /// <summary>
    /// Validates whether the window has enough input to run Start.
    /// </summary>
    private bool IsStartRequestValid()
    {
        return !string.IsNullOrWhiteSpace(_groupName) &&
               HasValidSelection() &&
               !string.IsNullOrWhiteSpace(_groupPath);
    }

    /// <summary>
    /// Maps current UI state to the backend start DTO.
    /// </summary>
    private StartJobRequest BuildStartRequest()
    {
        return new StartJobRequest
        {
            GroupPath = _groupPath,
            StartPipeline = _startPipeline,
            OnlySceneAnalysis = _onlySceneAnalysis,
            UseMockSceneAnalysis = _useMockSceneAnalysis,
            InteractionDescription = string.IsNullOrWhiteSpace(_interactionDescription) ? null : _interactionDescription,
            ScreensDir = string.IsNullOrWhiteSpace(_screensDir) ? null : _screensDir,
            AutoConfirmScene = false
        };
    }

    private void SaveServerUrl()
    {
        if (!TryApplyServerUrl(persist: true))
        {
            return;
        }

        _connectivityMessageType = MessageType.Info;
        _connectivityMessage = "Saved server URL: " + _serverUrl;
        Repaint();
    }

    /// <summary>
    /// Full start workflow:
    /// 1) export Unity scene artifacts, 2) build request, 3) start backend job, 4) start polling.
    /// </summary>
    private async void StartJobAsync()
    {
        if (_isStartingJob || _jobService == null)
        {
            return;
        }

        if (!TryApplyServerUrl(persist: false))
        {
            return;
        }

        _isStartingJob = true;
        _lastError = string.Empty;
        _cancelMessage = string.Empty;
        _statusMessage = "Preparing request...";

        try
        {
            if (!CollectSelectedObjects())
            {
                throw new InvalidOperationException("Select at least one GameObject before starting.");
            }

            _statusMessage = "Generating scene export...";
            if (!GenerateInteractionsWindow.TryGenerateInteractionAssets(
                    _selectedObjects,
                    _groupName,
                    _interactionDescription,
                    _startPipeline,
                    _onlySceneAnalysis,
                    _useMockSceneAnalysis,
                    out GenerateInteractionsWindow.GeneratedPaths generatedPaths,
                    out string generationError))
            {
                throw new InvalidOperationException("Asset generation failed: " + generationError);
            }

            ApplyGeneratedPaths(generatedPaths);
            ResetSceneConfirmationForNewGeneration();

            if (!string.IsNullOrWhiteSpace(_screensDir))
            {
                _statusMessage = "Copying screens...";
                CopyScreensToGroupDir(_screensDir, _groupPath);
                AssetDatabase.Refresh();
            }

            StopPolling();
            _jobService.Reset();
            _logsText = string.Empty;
            _resultText = string.Empty;
            _resultFetchedForCurrentJob = false;

            StartJobResponse startResponse = await _jobService.StartJobAsync(BuildStartRequest());
            _statusMessage = "Started job " + startResponse.JobId + ".";
            _logsText = _jobService.Logs;
            _scrollLogsToEnd = true;
            StartPolling();
        }
        catch (Exception ex)
        {
            _lastError = "Start failed: " + ex.Message;
            _statusMessage = "Idle";
        }
        finally
        {
            _isStartingJob = false;
            Repaint();
        }
    }

    /// <summary>
    /// Requests cancellation for the active backend job.
    /// </summary>
    private async void CancelJobAsync()
    {
        // Batch mode: signal the batch loop to stop after the current run is cancelled.
        if (_isBatchRunning)
        {
            _batchCancelRequested = true;
            _statusMessage = "Batch cancel requested.";
            Repaint();
        }

        if (_isCancellingJob || _jobService == null || !_jobService.HasJob || _jobService.IsInTerminalState)
        {
            return;
        }

        _isCancellingJob = true;
        _lastError = string.Empty;
        _statusMessage = "Sending cancel request...";

        try
        {
            CancelJobResponse cancelResponse = await _jobService.CancelJobAsync();
            _cancelMessage = cancelResponse.Message;
            _statusMessage = "Cancel requested.";
            if (cancelResponse.Status == JobStatus.CANCELLED)
            {
                CloseSceneReviewUi();
                StopPolling();
            }
            else
            {
                StartPolling();
            }
        }
        catch (Exception ex)
        {
            _lastError = "Cancel failed: " + ex.Message;
        }
        finally
        {
            _isCancellingJob = false;
            Repaint();
        }
    }

    /// <summary>
    /// Clears local UI/job state without touching generated files.
    /// </summary>
    private void ResetState()
    {
        StopPolling();
        _jobService?.Reset();
        ResetSceneConfirmationForNewGeneration();

        _statusMessage = "Idle";
        _lastError = string.Empty;
        _cancelMessage = string.Empty;
        _logsText = string.Empty;
        _resultText = string.Empty;
        _resultFetchedForCurrentJob = false;
        _connectivityMessage = string.Empty;
        _connectivityMessageType = MessageType.None;

        // Reset foldout states to defaults
        _foldScopeSelection = true;
        _foldInteractionSetup = true;
        _foldLogs = false;
        _foldResult = false;

        Repaint();
    }

    /// <summary>
    /// Verifies backend availability and optionally reports active job status.
    /// </summary>
    private async void TestConnectivityAsync()
    {
        if (_isTestingConnectivity || _apiClient == null || _jobService == null)
        {
            return;
        }

        if (!TryApplyServerUrl(persist: false))
        {
            return;
        }

        _isTestingConnectivity = true;
        _connectivityMessage = string.Empty;
        _connectivityMessageType = MessageType.None;

        try
        {
            HealthResponse health = await _apiClient.GetHealthAsync();
            string healthText = string.IsNullOrWhiteSpace(health.Status) ? "ok" : health.Status;

            if (string.IsNullOrWhiteSpace(_jobService.CurrentJobId))
            {
                _connectivityMessage = "Health OK (" + healthText + "). No job yet";
                _connectivityMessageType = MessageType.Info;
                return;
            }

            if (_isStatusPollInFlight)
            {
                _connectivityMessage = "Status request already in progress.";
                _connectivityMessageType = MessageType.Info;
                return;
            }

            JobStatusResponse status = await _jobService.PollStatusAsync();
            _connectivityMessage = "Health OK (" + healthText + "). Job " + status.JobId + " status: " + status.Status + ".";
            _connectivityMessageType = MessageType.Info;
        }
        catch (Exception ex)
        {
            _connectivityMessage = "Connectivity test failed: " + ex.Message;
            _connectivityMessageType = MessageType.Error;
        }
        finally
        {
            _isTestingConnectivity = false;
            Repaint();
        }
    }

    /// <summary>
    /// Normalizes/applies server URL to the API client and optionally persists it to EditorPrefs.
    /// </summary>
    private bool TryApplyServerUrl(bool persist)
    {
        if (_apiClient == null)
        {
            return false;
        }

        try
        {
            _serverUrl = NormalizeServerUrl(_serverUrl);
            _apiClient.SetServerUrl(_serverUrl);

            if (persist)
            {
                EditorPrefs.SetString(ServerUrlEditorPrefKey, _serverUrl);
            }

            return true;
        }
        catch (Exception ex)
        {
            _lastError = "Invalid server URL: " + ex.Message;
            _statusMessage = "Idle";
            return false;
        }
    }

    private static string NormalizeServerUrl(string value)
    {
        string trimmed = string.IsNullOrWhiteSpace(value) ? DefaultServerUrl : value.Trim();
        if (!trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = "http://" + trimmed;
        }
        return trimmed.TrimEnd('/');
    }

    /// <summary>
    /// Starts editor update loop that polls status/logs.
    /// </summary>
    private void StartPolling()
    {
        if (_isPolling)
        {
            return;
        }

        _isPolling = true;
        _nextStatusPollAt = 0d;
        _nextLogsPollAt = 0d;
        _nextSceneReviewPollAt = 0d;
        EditorApplication.update += OnEditorUpdate;
    }

    /// <summary>
    /// Stops editor update loop polling.
    /// </summary>
    private void StopPolling()
    {
        if (!_isPolling)
        {
            return;
        }

        _isPolling = false;
        _isStatusPollInFlight = false;
        _isLogsPollInFlight = false;
        _isSceneReviewPollInFlight = false;
        EditorApplication.update -= OnEditorUpdate;
    }

    /// <summary>
    /// Polling scheduler called by Unity editor update.
    /// </summary>
    private void OnEditorUpdate()
    {
        if (!_isPolling || _jobService == null || !_jobService.HasJob)
        {
            return;
        }

        if (_jobService.IsInTerminalState)
        {
            if (_jobService.CanFetchResult && !_resultFetchedForCurrentJob)
            {
                _ = FetchResultOnceAsync();
            }

            StopPolling();
            Repaint();
            return;
        }

        double now = EditorApplication.timeSinceStartup;

        if (now >= _nextStatusPollAt && !_isStatusPollInFlight)
        {
            _nextStatusPollAt = now + StatusPollIntervalSeconds;
            _ = PollStatusAsync();
        }

        JobStatusResponse status = _jobService.LastKnownStatus;
        bool shouldPollLogs = status != null && status.Status == JobStatus.RUNNING;
        if (shouldPollLogs && now >= _nextLogsPollAt && !_isLogsPollInFlight)
        {
            _nextLogsPollAt = now + LogsPollIntervalSeconds;
            _ = PollLogsAsync();
        }

        if (now >= _nextSceneReviewPollAt && !_isSceneReviewPollInFlight)
        {
            _nextSceneReviewPollAt = now + SceneReviewPollIntervalSeconds;
            _ = PollSceneReviewAsync();
        }

        // Keep repainting while active for spinner animation
        Repaint();
    }

    /// <summary>
    /// Polls backend job status and handles terminal transitions.
    /// </summary>
    private async Task PollStatusAsync()
    {
        if (_jobService == null || _isStatusPollInFlight)
        {
            return;
        }

        _isStatusPollInFlight = true;

        try
        {
            JobStatusResponse status = await _jobService.PollStatusAsync();
            _statusMessage = GetPhaseDisplayText(status.Phase, _sceneReviewState);

            if (VivianJobService.IsTerminalStatus(status.Status))
            {
                if (status.Status == JobStatus.CANCELLED)
                {
                    CloseSceneReviewUi();
                }

                if (status.Status == JobStatus.SUCCEEDED)
                {
                    await FetchResultOnceAsync();
                }

                StopPolling();
            }
        }
        catch (Exception ex)
        {
            HandlePollingFailure("Status polling failed", ex);
        }
        finally
        {
            _isStatusPollInFlight = false;
            Repaint();
        }
    }

    /// <summary>
    /// Polls incremental logs from the backend.
    /// </summary>
    private async Task PollLogsAsync()
    {
        if (_jobService == null || _isLogsPollInFlight)
        {
            return;
        }

        _isLogsPollInFlight = true;

        try
        {
            await _jobService.PollLogsAsync();
        }
        catch (Exception ex)
        {
            HandlePollingFailure("Logs polling failed", ex);
        }
        finally
        {
            _isLogsPollInFlight = false;
            Repaint();
        }
    }

    /// <summary>
    /// Polls scene-review payload for revision-gated confirmation UI.
    /// </summary>
    private async Task PollSceneReviewAsync()
    {
        if (_jobService == null || _isSceneReviewPollInFlight || !_jobService.HasJob || _jobService.IsInTerminalState)
        {
            return;
        }

        _isSceneReviewPollInFlight = true;

        try
        {
            SceneReviewResponse review = await _jobService.PollSceneReviewAsync();
            ApplySceneReviewResponse(review);
        }
        catch (Exception ex)
        {
            HandlePollingFailure("Scene review polling failed", ex);
        }
        finally
        {
            _isSceneReviewPollInFlight = false;
            Repaint();
        }
    }

    private async Task SubmitSceneReviewDecisionAsync(bool confirmed, string feedback)
    {
        if (_jobService == null || _isSceneDecisionInFlight || !_hasSceneReviewRevision)
        {
            return;
        }

        _isSceneDecisionInFlight = true;
        _sceneReviewMessage = string.Empty;

        try
        {
            int revision = _sceneReviewRevision;
            SceneReviewDecisionResponse response = await _jobService.SubmitSceneReviewDecisionAsync(revision, confirmed, feedback);
            _sceneReviewState = response.ReviewState;
            _sceneReviewMessage = string.IsNullOrWhiteSpace(response.Message)
                ? "Scene review decision accepted."
                : response.Message;

            if (confirmed)
            {
                _sceneConfirmedForCurrentJob = true;
                _sceneDecisionLocked = true;
            }
            else
            {
                _sceneDecisionAcceptedRevision = response.AcceptedRevision;
                _sceneDecisionLocked = true;
            }
        }
        catch (VivianApiException ex) when (ex.StatusCode == 409)
        {
            _sceneReviewMessage = "Scene review revision changed. Refreshing...";
            if (_jobService.HasJob && !_jobService.IsInTerminalState)
            {
                try
                {
                    SceneReviewResponse latest = await _jobService.PollSceneReviewAsync();
                    ApplySceneReviewResponse(latest);
                }
                catch (Exception refreshEx)
                {
                    _sceneReviewMessage = "Scene review refresh failed: " + refreshEx.Message;
                }
            }
        }
        catch (Exception ex)
        {
            _sceneReviewMessage = "Scene review submit failed: " + ex.Message;
        }
        finally
        {
            _isSceneDecisionInFlight = false;
            Repaint();
        }
    }

    /// <summary>
    /// Fetches result exactly once for a succeeded job.
    /// </summary>
    private async Task FetchResultOnceAsync()
    {
        if (_jobService == null || _isFetchingResult || _resultFetchedForCurrentJob || !_jobService.CanFetchResult)
        {
            return;
        }

        _isFetchingResult = true;

        try
        {
            try
            {
                JobResultResponse result = await _jobService.FetchResultAsync();
                _resultText = JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch
            {
                _resultText = await _jobService.FetchResultJsonAsync();
            }

            _resultFetchedForCurrentJob = true;
            _statusMessage = "Job succeeded. Result fetched.";
        }
        catch (Exception ex)
        {
            _lastError = "Result fetch failed: " + ex.Message;
        }
        finally
        {
            _isFetchingResult = false;
            Repaint();
        }
    }

    private void HandlePollingFailure(string prefix, Exception ex)
    {
        _lastError = prefix + ": " + ex.Message;
        _statusMessage = "Polling stopped. Verify server URL and backend availability.";
        StopPolling();
    }
}
#endif
