#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using Vivian.Backend.Client;
using Vivian.Backend.Dtos;

/// <summary>
/// Unified editor window for the Vivian backend workflow.
/// It combines scene selection/export, backend job control, logs/results, and scene confirmation chat.
/// </summary>
public sealed class VivianBackendWindow : EditorWindow
{
    private const string ServerUrlEditorPrefKey = "VivianBackend.ServerUrl";
    private const string DefaultServerUrl = "http://127.0.0.1:8000";

    private const double StatusPollIntervalSeconds = 0.5;
    private const double LogsPollIntervalSeconds = 0.75d;

    private VivianApiClient _apiClient;
    private VivianJobService _jobService;

    private string _serverUrl = DefaultServerUrl;
    private string _sceneJsonPath = string.Empty;
    private string _viewsManifestPath = string.Empty;
    private string _sceneDir = string.Empty;
    private bool _startPipeline = true;
    private bool _onlySceneAnalysis;
    private bool _useMockSceneAnalysis;

    private readonly Dictionary<GameObject, bool> _selection = new Dictionary<GameObject, bool>();
    private readonly List<GameObject> _selectedObjects = new List<GameObject>();
    private Vector2 _selectionScroll;
    private bool _showChildObjects;
    private string _groupName = string.Empty;
    private string _interactionDescription = string.Empty;
    private string _groupPath = string.Empty;

    private enum ChatRole
    {
        Agent,
        User
    }

    private struct ChatMessage
    {
        public ChatRole Role;
        public string Text;
    }

    private readonly List<ChatMessage> _chatMessages = new List<ChatMessage>();
    private Vector2 _chatScroll;
    private string _userChatInput = string.Empty;
    private string _sceneSummaryText = string.Empty;
    private string _sceneFeedbackText = string.Empty;
    private DateTime _sceneSummaryLastWrite = DateTime.MinValue;

    private string _statusMessage = "Idle";
    private string _lastError = string.Empty;
    private string _cancelMessage = string.Empty;
    private string _logsText = string.Empty;
    private string _resultText = string.Empty;

    private string _connectivityMessage = string.Empty;
    private MessageType _connectivityMessageType = MessageType.None;

    private Vector2 _mainScroll;
    private Vector2 _logsScroll;
    private Vector2 _resultScroll;
    private bool _scrollLogsToEnd;

    private bool _isPolling;
    private bool _isStatusPollInFlight;
    private bool _isLogsPollInFlight;
    private bool _isStartingJob;
    private bool _isCancellingJob;
    private bool _isTestingConnectivity;
    private bool _isFetchingResult;
    private bool _resultFetchedForCurrentJob;

    private double _nextStatusPollAt;
    private double _nextLogsPollAt;

    [MenuItem("Vivian/Backend Job Window")]
    private static void ShowWindow()
    {
        GetWindow<VivianBackendWindow>("Vivian Backend");
    }

    private void OnEnable()
    {
        _serverUrl = EditorPrefs.GetString(ServerUrlEditorPrefKey, DefaultServerUrl);
        _apiClient = new VivianApiClient(_serverUrl, TimeSpan.FromSeconds(45));
        _jobService = new VivianJobService(_apiClient);
        SubscribeToServiceEvents();
        SyncAutoFilledPaths();
    }

    private void OnDisable()
    {
        StopPolling();
        UnsubscribeFromServiceEvents();

        if (_apiClient != null)
        {
            _apiClient.Dispose();
            _apiClient = null;
        }

        _jobService = null;
    }

    private void OnGUI()
    {
        if (_apiClient == null || _jobService == null)
        {
            EditorGUILayout.HelpBox("Backend window is not initialized.", MessageType.Warning);
            if (GUILayout.Button("Reinitialize"))
            {
                OnEnable();
            }
            return;
        }

        _mainScroll = EditorGUILayout.BeginScrollView(_mainScroll);
        // Render the full workflow in execution order from request setup to outputs.
        DrawServerSection();
        EditorGUILayout.Space();
        DrawScopeSelectionSection();
        EditorGUILayout.Space();
        DrawInteractionInputSection();
        EditorGUILayout.Space();
        DrawStartRequestSection();
        EditorGUILayout.Space();
        DrawControlsSection();
        EditorGUILayout.Space();
        DrawStatusSection();
        EditorGUILayout.Space();
        DrawLogsSection();
        EditorGUILayout.Space();
        DrawSceneConfirmationSection();
        EditorGUILayout.Space();
        DrawResultSection();
        EditorGUILayout.EndScrollView();
    }

    /// <summary>
    /// Backend base URL settings and health probe.
    /// </summary>
    private void DrawServerSection()
    {
        EditorGUILayout.LabelField("Backend", EditorStyles.boldLabel);
        EditorGUILayout.BeginHorizontal();
        _serverUrl = EditorGUILayout.TextField("Server URL", _serverUrl);

        if (GUILayout.Button("Save", GUILayout.Width(80)))
        {
            SaveServerUrl();
        }

        EditorGUI.BeginDisabledGroup(_isTestingConnectivity);
        if (GUILayout.Button("Test", GUILayout.Width(80)))
        {
            TestConnectivityAsync();
        }
        EditorGUI.EndDisabledGroup();
        EditorGUILayout.EndHorizontal();

        if (_connectivityMessageType != MessageType.None && !string.IsNullOrEmpty(_connectivityMessage))
        {
            EditorGUILayout.HelpBox(_connectivityMessage, _connectivityMessageType);
        }
    }

    /// <summary>
    /// Scene object scope and object-level selection.
    /// </summary>
    private void DrawScopeSelectionSection()
    {
        EditorGUILayout.LabelField("Select Scope", EditorStyles.boldLabel);

        Scene scene = SceneManager.GetActiveScene();
        if (!scene.IsValid())
        {
            EditorGUILayout.HelpBox("No active scene loaded.", MessageType.Warning);
            return;
        }

        var allObjects = new List<GameObject>();
        if (_showChildObjects)
        {
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                CollectChildren(root, allObjects);
            }
        }
        else
        {
            allObjects.AddRange(scene.GetRootGameObjects());
        }

        EditorGUILayout.BeginHorizontal();
        string toggleLabel = _showChildObjects ? "Show Top-Level Only" : "Show Children";
        if (GUILayout.Button(toggleLabel, GUILayout.Width(160)))
        {
            _showChildObjects = !_showChildObjects;
            if (!_showChildObjects)
            {
                PruneSelectionToRootObjects(scene);
            }
        }

        GUILayout.FlexibleSpace();
        EditorGUILayout.LabelField("Selected: " + GetSelectionCount(), GUILayout.Width(110));
        EditorGUILayout.EndHorizontal();

        _selectionScroll = EditorGUILayout.BeginScrollView(_selectionScroll, GUILayout.Height(220));

        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("Select", GUILayout.Width(50));
        EditorGUILayout.LabelField("GameObject");
        EditorGUILayout.LabelField("Active", GUILayout.Width(50));
        EditorGUILayout.EndHorizontal();

        foreach (GameObject go in allObjects)
        {
            if (go == null)
            {
                continue;
            }

            EditorGUILayout.BeginHorizontal();

            bool isSelected = _selection.ContainsKey(go) && _selection[go];
            bool newSelected = EditorGUILayout.Toggle(isSelected, GUILayout.Width(50));
            if (newSelected != isSelected)
            {
                _selection[go] = newSelected;
            }

            EditorGUILayout.ObjectField(go, typeof(GameObject), true);
            EditorGUILayout.Toggle(go.activeInHierarchy, GUILayout.Width(50));
            EditorGUILayout.EndHorizontal();
        }

        EditorGUILayout.EndScrollView();
    }

    /// <summary>
    /// User inputs that define the export payload and backend prompt context.
    /// </summary>
    private void DrawInteractionInputSection()
    {
        EditorGUILayout.LabelField("Interaction Setup", EditorStyles.boldLabel);

        EditorGUI.BeginChangeCheck();
        string newGroupName = EditorGUILayout.TextField("Group Name", _groupName);
        if (EditorGUI.EndChangeCheck())
        {
            _groupName = newGroupName;
            SyncAutoFilledPaths();
        }

        EditorGUILayout.LabelField("Interaction Description");
        _interactionDescription = EditorGUILayout.TextArea(_interactionDescription, GUILayout.MinHeight(60));

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Pipeline Options", EditorStyles.boldLabel);
        _startPipeline = EditorGUILayout.ToggleLeft("Start Pipeline", _startPipeline);
        EditorGUI.BeginDisabledGroup(!_startPipeline);
        _onlySceneAnalysis = EditorGUILayout.ToggleLeft("Only Scene Analysis", _onlySceneAnalysis);
        _useMockSceneAnalysis = EditorGUILayout.ToggleLeft("Use Mock Scene Analysis", _useMockSceneAnalysis);
        EditorGUI.EndDisabledGroup();
    }

    /// <summary>
    /// Read-only request values derived from the selected group name.
    /// </summary>
    private void DrawStartRequestSection()
    {
        EditorGUILayout.LabelField("Start Request (Auto-Filled)", EditorStyles.boldLabel);

        EditorGUI.BeginDisabledGroup(true);
        EditorGUILayout.TextField("group_path", _groupPath);
        EditorGUILayout.TextField("Generated scene.json", _sceneJsonPath);
        EditorGUILayout.TextField("Generated views_manifest.json", _viewsManifestPath);
        EditorGUILayout.TextField("Generated scene dir", _sceneDir);
        EditorGUI.EndDisabledGroup();

        if (string.IsNullOrWhiteSpace(_groupName))
        {
            EditorGUILayout.HelpBox("Enter a group name to generate group_path.", MessageType.Info);
        }
    }

    /// <summary>
    /// Main execution controls for start/cancel/reset.
    /// </summary>
    private void DrawControlsSection()
    {
        bool canStart = !_isStartingJob &&
                        !_isCancellingJob &&
                        !_isFetchingResult &&
                        !_jobService.IsJobActive &&
                        !_jobService.IsCancelPending &&
                        IsStartRequestValid();

        bool canCancel = !_isCancellingJob &&
                         _jobService.HasJob &&
                         !_jobService.IsInTerminalState;

        EditorGUILayout.BeginHorizontal();

        EditorGUI.BeginDisabledGroup(!canStart);
        if (GUILayout.Button(_isStartingJob ? "Starting..." : "Start"))
        {
            StartJobAsync();
        }
        EditorGUI.EndDisabledGroup();

        EditorGUI.BeginDisabledGroup(!canCancel);
        if (GUILayout.Button(_isCancellingJob ? "Cancelling..." : "Cancel"))
        {
            CancelJobAsync();
        }
        EditorGUI.EndDisabledGroup();

        if (GUILayout.Button("Reset"))
        {
            ResetState();
        }

        EditorGUILayout.EndHorizontal();
    }

    /// <summary>
    /// Shows generated scene summary text and writes user feedback for the backend.
    /// </summary>
    private void DrawSceneConfirmationSection()
    {
        EditorGUILayout.LabelField("Scene Confirmation Chat", EditorStyles.boldLabel);
        UpdateSceneSummaryIfNeeded();
        string summaryText = string.IsNullOrEmpty(_sceneSummaryText) ? "(no summary yet)" : _sceneSummaryText;
        string summaryPath = GetSceneSummaryPath();
        bool canSendFeedback = !string.IsNullOrEmpty(_groupPath) && Directory.Exists(_groupPath);

        EditorGUILayout.LabelField("Summary file:", string.IsNullOrEmpty(summaryPath) ? "(not available)" : summaryPath);

        var bubbleStyle = new GUIStyle(EditorStyles.helpBox)
        {
            wordWrap = true,
            padding = new RectOffset(10, 10, 8, 8)
        };
        float maxBubbleWidth = Mathf.Max(200f, EditorGUIUtility.currentViewWidth * 0.62f);

        _chatScroll = EditorGUILayout.BeginScrollView(_chatScroll, GUILayout.Height(220));
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("Scene understanding summary:\n" + summaryText, bubbleStyle, GUILayout.MaxWidth(maxBubbleWidth));
        GUILayout.FlexibleSpace();
        EditorGUILayout.EndHorizontal();
        EditorGUILayout.Space(4);

        foreach (ChatMessage message in _chatMessages)
        {
            bool isUser = message.Role == ChatRole.User;
            EditorGUILayout.BeginHorizontal();
            if (isUser)
            {
                GUILayout.FlexibleSpace();
            }
            EditorGUILayout.LabelField(message.Text ?? string.Empty, bubbleStyle, GUILayout.MaxWidth(maxBubbleWidth));
            if (!isUser)
            {
                GUILayout.FlexibleSpace();
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space(4);
        }
        EditorGUILayout.EndScrollView();

        EditorGUILayout.BeginHorizontal();
        _userChatInput = EditorGUILayout.TextField(_userChatInput, GUILayout.ExpandWidth(true));
        EditorGUI.BeginDisabledGroup(!canSendFeedback);
        if (GUILayout.Button("Send", GUILayout.Width(80)))
        {
            string trimmed = _userChatInput == null ? string.Empty : _userChatInput.Trim();
            if (!string.IsNullOrEmpty(trimmed))
            {
                OnUserMessageSubmitted(trimmed);
                _sceneFeedbackText = trimmed;
                WriteSceneFeedback(false);
            }
        }
        if (GUILayout.Button("Confirm Scene Analysis", GUILayout.Width(170)))
        {
            string trimmed = _userChatInput == null ? string.Empty : _userChatInput.Trim();
            if (!string.IsNullOrEmpty(trimmed))
            {
                OnUserMessageSubmitted(trimmed);
                _sceneFeedbackText = trimmed;
            }
            else
            {
                _sceneFeedbackText = string.Empty;
            }
            WriteSceneFeedback(true);
        }
        EditorGUI.EndDisabledGroup();
        EditorGUILayout.EndHorizontal();

        if (!canSendFeedback)
        {
            EditorGUILayout.HelpBox("Start a run first to generate the scene folder for summary and feedback files.", MessageType.Info);
        }
    }

    /// <summary>
    /// Current backend job state snapshot.
    /// </summary>
    private void DrawStatusSection()
    {
        EditorGUILayout.LabelField("Status", EditorStyles.boldLabel);

        JobStatusResponse status = _jobService.LastKnownStatus;
        string jobId = _jobService.HasJob ? _jobService.CurrentJobId : "-";
        string state = status != null ? status.Status.ToString() : "Idle";
        string progress = GetProgressText(status);
        string message = GetStatusMessage(status);

        EditorGUILayout.LabelField("Job ID", jobId);
        EditorGUILayout.LabelField("State", state);
        EditorGUILayout.LabelField("Progress", progress);
        EditorGUILayout.LabelField("Message", message);

        if (!string.IsNullOrWhiteSpace(_lastError))
        {
            EditorGUILayout.HelpBox(_lastError, MessageType.Error);
        }

        if (status != null && status.Status == JobStatus.FAILED && !string.IsNullOrWhiteSpace(status.Error))
        {
            EditorGUILayout.HelpBox("Job failed: " + status.Error, MessageType.Error);
        }
    }

    /// <summary>
    /// Incremental backend log output.
    /// </summary>
    private void DrawLogsSection()
    {
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("Logs", EditorStyles.boldLabel);
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("Clear Logs", GUILayout.Width(100)))
        {
            _jobService.ClearLogs();
        }
        EditorGUILayout.EndHorizontal();

        if (_scrollLogsToEnd)
        {
            _logsScroll.y = float.MaxValue;
            _scrollLogsToEnd = false;
        }

        _logsScroll = EditorGUILayout.BeginScrollView(_logsScroll, GUILayout.Height(240));
        EditorGUILayout.TextArea(string.IsNullOrEmpty(_logsText) ? "(no logs yet)" : _logsText, GUILayout.ExpandHeight(true));
        EditorGUILayout.EndScrollView();
    }

    /// <summary>
    /// Final backend result payload.
    /// </summary>
    private void DrawResultSection()
    {
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("Result", EditorStyles.boldLabel);
        GUILayout.FlexibleSpace();
        EditorGUI.BeginDisabledGroup(string.IsNullOrEmpty(_resultText));
        if (GUILayout.Button("Copy Result to Clipboard", GUILayout.Width(190)))
        {
            EditorGUIUtility.systemCopyBuffer = _resultText;
        }
        EditorGUI.EndDisabledGroup();
        EditorGUILayout.EndHorizontal();

        _resultScroll = EditorGUILayout.BeginScrollView(_resultScroll, GUILayout.Height(200));
        EditorGUILayout.TextArea(string.IsNullOrEmpty(_resultText) ? "(no result yet)" : _resultText, GUILayout.ExpandHeight(true));
        EditorGUILayout.EndScrollView();
    }

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
            UseMockSceneAnalysis = _useMockSceneAnalysis
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
            // Generate scene.json and views_manifest.json from the current Unity selection.
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

            // Ensure no stale state from a previous job leaks into this run.
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
            StartPolling();
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

        _statusMessage = "Idle";
        _lastError = string.Empty;
        _cancelMessage = string.Empty;
        _logsText = string.Empty;
        _resultText = string.Empty;
        _resultFetchedForCurrentJob = false;
        _connectivityMessage = string.Empty;
        _connectivityMessageType = MessageType.None;

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
            _statusMessage = "Job state: " + status.Status;

            if (VivianJobService.IsTerminalStatus(status.Status))
            {
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

    /// <summary>
    /// Wires job service events into window state.
    /// </summary>
    private void SubscribeToServiceEvents()
    {
        if (_jobService == null)
        {
            return;
        }

        _jobService.StatusChanged += OnServiceStatusChanged;
        _jobService.LogsAppended += OnServiceLogsAppended;
        _jobService.Completed += OnServiceCompleted;
        _jobService.Failed += OnServiceFailed;
        _jobService.LogsCleared += OnServiceLogsCleared;
    }

    /// <summary>
    /// Unwires job service events during teardown.
    /// </summary>
    private void UnsubscribeFromServiceEvents()
    {
        if (_jobService == null)
        {
            return;
        }

        _jobService.StatusChanged -= OnServiceStatusChanged;
        _jobService.LogsAppended -= OnServiceLogsAppended;
        _jobService.Completed -= OnServiceCompleted;
        _jobService.Failed -= OnServiceFailed;
        _jobService.LogsCleared -= OnServiceLogsCleared;
    }

    private void OnServiceStatusChanged(JobStatusResponse status)
    {
        if (status == null)
        {
            return;
        }

        _statusMessage = "Job state: " + status.Status;
        if (status.Status == JobStatus.CANCELLED)
        {
            _statusMessage = "Job cancelled.";
        }

        if (status.Status == JobStatus.FAILED && !string.IsNullOrWhiteSpace(status.Error))
        {
            _lastError = "Job failed: " + status.Error;
        }

        Repaint();
    }

    private void OnServiceLogsAppended(string chunk)
    {
        _logsText = _jobService != null ? _jobService.Logs : string.Empty;
        _scrollLogsToEnd = true;
        Repaint();
    }

    private void OnServiceCompleted(JobResultResponse result)
    {
        _statusMessage = "Job completed successfully.";
        Repaint();
    }

    private void OnServiceFailed(JobStatusResponse failedStatus)
    {
        string details = failedStatus != null && !string.IsNullOrWhiteSpace(failedStatus.Error)
            ? failedStatus.Error
            : "Unknown error.";
        _lastError = "Job failed: " + details;
        Repaint();
    }

    private void OnServiceLogsCleared()
    {
        _logsText = string.Empty;
        Repaint();
    }

    private string GetStatusMessage(JobStatusResponse status)
    {
        if (!string.IsNullOrWhiteSpace(_lastError))
        {
            return _lastError;
        }

        if (_jobService != null && _jobService.IsCancelPending)
        {
            return string.IsNullOrWhiteSpace(_cancelMessage) ? "Cancel requested..." : _cancelMessage;
        }

        if (status != null && !string.IsNullOrWhiteSpace(status.Error))
        {
            return status.Error;
        }

        return string.IsNullOrWhiteSpace(_statusMessage) ? "-" : _statusMessage;
    }

    private static string GetProgressText(JobStatusResponse status)
    {
        if (status == null)
        {
            return "-";
        }

        switch (status.Status)
        {
            case JobStatus.QUEUED:
                return "0%";
            case JobStatus.RUNNING:
                return "50%";
            case JobStatus.SUCCEEDED:
            case JobStatus.FAILED:
            case JobStatus.CANCELLED:
                return "100%";
            default:
                return "-";
        }
    }

    /// <summary>
    /// Depth-first flattening of hierarchy for "Show Children" selection mode.
    /// </summary>
    private void CollectChildren(GameObject go, List<GameObject> list)
    {
        list.Add(go);
        for (int i = 0; i < go.transform.childCount; i++)
        {
            CollectChildren(go.transform.GetChild(i).gameObject, list);
        }
    }

    /// <summary>
    /// Drops non-root selected objects when switching back to top-level scope.
    /// </summary>
    private void PruneSelectionToRootObjects(Scene scene)
    {
        var rootObjects = new HashSet<GameObject>(scene.GetRootGameObjects());
        var keys = new List<GameObject>(_selection.Keys);
        foreach (GameObject key in keys)
        {
            if (key == null || !rootObjects.Contains(key))
            {
                _selection.Remove(key);
            }
        }
    }

    private int GetSelectionCount()
    {
        int count = 0;
        foreach (KeyValuePair<GameObject, bool> kv in _selection)
        {
            if (kv.Key != null && kv.Value)
            {
                count++;
            }
        }
        return count;
    }

    private bool HasValidSelection()
    {
        foreach (KeyValuePair<GameObject, bool> kv in _selection)
        {
            if (kv.Key != null && kv.Value)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Builds the list used for export from current checkbox selections.
    /// </summary>
    private bool CollectSelectedObjects()
    {
        _selectedObjects.Clear();
        foreach (KeyValuePair<GameObject, bool> kv in _selection)
        {
            if (kv.Key != null && kv.Value)
            {
                _selectedObjects.Add(kv.Key);
            }
        }

        return _selectedObjects.Count > 0;
    }

    /// <summary>
    /// Recomputes generated file paths from the current group name.
    /// </summary>
    private void SyncAutoFilledPaths()
    {
        if (!GenerateInteractionsWindow.TryBuildGeneratedPaths(_groupName, out GenerateInteractionsWindow.GeneratedPaths paths))
        {
            _groupPath = string.Empty;
            _sceneJsonPath = string.Empty;
            _viewsManifestPath = string.Empty;
            _sceneDir = string.Empty;
            return;
        }

        ApplyGeneratedPaths(paths);
    }

    private void ApplyGeneratedPaths(GenerateInteractionsWindow.GeneratedPaths paths)
    {
        _groupPath = paths.GroupPath ?? string.Empty;
        _sceneJsonPath = paths.SceneJsonPath ?? string.Empty;
        _viewsManifestPath = paths.ViewsManifestPath ?? string.Empty;
        _sceneDir = paths.SceneDir ?? string.Empty;
    }

    /// <summary>
    /// Clears chat/summary state when a new generation starts.
    /// </summary>
    private void ResetSceneConfirmationForNewGeneration()
    {
        _sceneSummaryText = string.Empty;
        _sceneSummaryLastWrite = DateTime.MinValue;
        _sceneFeedbackText = string.Empty;
        _chatMessages.Clear();
        _userChatInput = string.Empty;
        _chatScroll = Vector2.zero;
    }

    /// <summary>
    /// Reloads summary file when it changes on disk.
    /// </summary>
    private void UpdateSceneSummaryIfNeeded()
    {
        string summaryPath = GetSceneSummaryPath();
        if (string.IsNullOrEmpty(summaryPath) || !File.Exists(summaryPath))
        {
            return;
        }

        DateTime lastWrite = File.GetLastWriteTimeUtc(summaryPath);
        if (lastWrite <= _sceneSummaryLastWrite)
        {
            return;
        }

        try
        {
            _sceneSummaryText = File.ReadAllText(summaryPath);
            _sceneSummaryLastWrite = lastWrite;
            Repaint();
        }
        catch (Exception ex)
        {
            Debug.LogWarning("Failed to read scene summary: " + ex.Message);
        }
    }

    private string GetSceneSummaryPath()
    {
        if (string.IsNullOrEmpty(_groupPath))
        {
            return string.Empty;
        }
        return Path.Combine(_groupPath, "scene_understanding_summary.txt");
    }

    private string GetSceneFeedbackPath()
    {
        if (string.IsNullOrEmpty(_groupPath))
        {
            return string.Empty;
        }
        return Path.Combine(_groupPath, "scene_feedback.json");
    }

    /// <summary>
    /// Writes scene feedback JSON for the backend process.
    /// </summary>
    private void WriteSceneFeedback(bool confirmed)
    {
        string feedbackPath = GetSceneFeedbackPath();
        if (string.IsNullOrEmpty(feedbackPath))
        {
            Debug.LogWarning("Scene feedback path is not available yet.");
            return;
        }

        string feedback = string.IsNullOrWhiteSpace(_sceneFeedbackText) ? string.Empty : _sceneFeedbackText.Trim();

        try
        {
            string tempPath = feedbackPath + ".tmp";
            string json = BuildSceneFeedbackJson(confirmed, feedback);
            var utf8NoBom = new System.Text.UTF8Encoding(false);
            // Write-then-move avoids partially written feedback files.
            File.WriteAllText(tempPath, json, utf8NoBom);
            if (File.Exists(feedbackPath))
            {
                File.Delete(feedbackPath);
            }
            File.Move(tempPath, feedbackPath);
            Debug.Log("Wrote scene feedback: " + feedbackPath);
            if (confirmed)
            {
                _sceneFeedbackText = string.Empty;
            }
        }
        catch (Exception ex)
        {
            Debug.LogError("Failed to write scene feedback: " + ex.Message);
        }
    }

    /// <summary>
    /// Minimal JSON payload expected by the backend scene confirmation step.
    /// </summary>
    private static string BuildSceneFeedbackJson(bool confirmed, string feedback)
    {
        string confirmedText = confirmed ? "true" : "false";
        if (string.IsNullOrEmpty(feedback))
        {
            return "{\n  \"confirmed\": " + confirmedText + "\n}";
        }

        return "{\n  \"confirmed\": " + confirmedText + ",\n  \"feedback\": \"" + EscapeJsonString(feedback) + "\"\n}";
    }

    private static string EscapeJsonString(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\r", "\\r")
            .Replace("\n", "\\n")
            .Replace("\t", "\\t");
    }

    /// <summary>
    /// Adds a user chat message and resets input.
    /// </summary>
    private void OnUserMessageSubmitted(string text)
    {
        _chatMessages.Add(new ChatMessage { Role = ChatRole.User, Text = text });
        _userChatInput = string.Empty;
        _chatScroll.y = float.MaxValue;
        GUI.FocusControl(null);
    }

    /// <summary>
    /// Adds an agent message to the chat panel.
    /// </summary>
    public void AddAgentResponse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        _chatMessages.Add(new ChatMessage { Role = ChatRole.Agent, Text = text.Trim() });
        _chatScroll.y = float.MaxValue;
        Repaint();
    }

    /// <summary>
    /// Convenience entry for external code to post agent chat text into the open backend window.
    /// </summary>
    public static bool TryAddAgentResponse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        VivianBackendWindow[] windows = Resources.FindObjectsOfTypeAll<VivianBackendWindow>();
        if (windows == null || windows.Length == 0)
        {
            return false;
        }

        windows[0].AddAgentResponse(text);
        return true;
    }
}
#endif
