#if UNITY_EDITOR
using UnityEditor;
using Vivian.Backend.Dtos;

public sealed partial class VivianBackendWindow
{
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

        _statusMessage = GetPhaseDisplayText(status.Phase, _sceneReviewState);

        // Auto-collapse/expand logic based on pipeline phase transitions
        if (status.Status == JobStatus.RUNNING)
        {
            // Collapse setup sections, expand logs when job is running
            _foldScopeSelection = false;
            _foldInteractionSetup = false;
            _foldLogs = true;
        }

        if (status.Status == JobStatus.CANCELLED)
        {
            _statusMessage = "Cancelled.";
            CloseSceneReviewUi();
        }

        if (status.Status == JobStatus.FAILED && !string.IsNullOrWhiteSpace(status.Error))
        {
            _lastError = "Job failed: " + ShortError(status.Error);
            // Keep logs open on failure for debugging
            _foldLogs = true;
        }

        if (status.Status == JobStatus.SUCCEEDED)
        {
            // Auto-open result, collapse logs
            _foldResult = true;
            _foldLogs = false;
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
        _foldResult = true;
        Repaint();
    }

    private void OnServiceFailed(JobStatusResponse failedStatus)
    {
        string details = failedStatus != null && !string.IsNullOrWhiteSpace(failedStatus.Error)
            ? ShortError(failedStatus.Error)
            : "Unknown error.";
        _lastError = "Job failed: " + details;
        _foldLogs = true;
        Repaint();
    }

    private void OnServiceLogsCleared()
    {
        _logsText = string.Empty;
        Repaint();
    }
}
#endif
