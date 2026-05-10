#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;
using Vivian.Backend.Dtos;
using Vivian.Editor.Models;

public sealed partial class VivianBackendWindow
{
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

        if (status != null)
        {
            return GetPhaseDisplayText(status.Phase, _sceneReviewState);
        }

        return string.IsNullOrWhiteSpace(_statusMessage) ? "-" : _statusMessage;
    }

    private static string ShortError(string error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return error;
        }
        int tracebackIndex = error.IndexOf("\nTraceback ", StringComparison.Ordinal);
        string trimmed = tracebackIndex >= 0 ? error.Substring(0, tracebackIndex) : error;
        return trimmed.Split('\n')[0].Trim();
    }

    private static string GetProgressText(JobStatusResponse status)
    {
        if (status == null)
        {
            return "-";
        }

        switch (status.Phase)
        {
            case JobPhase.QUEUED:                                  return "1/16";
            case JobPhase.PREPARING_INPUT:                         return "2/16";
            case JobPhase.ANALYZING_SCENE:                         return "3/16";
            case JobPhase.AWAITING_SCENE_CONFIRMATION:             return "4/16";
            case JobPhase.PLANNING_INTERACTIONS:                   return "5/16";
            case JobPhase.GENERATING_SPECS_INTERACTION_ELEMENTS:   return "6/16";
            case JobPhase.GENERATING_SPECS_VISUALIZATION_ELEMENTS: return "7/16";
            case JobPhase.GENERATING_SPECS_STATES:                 return "8/16";
            case JobPhase.GENERATING_SPECS_TRANSITIONS:            return "9/16";
            case JobPhase.GENERATING_SPECS:                        return "10/16";
            case JobPhase.REVIEWING_CONSISTENCY:                   return "11/16";
            case JobPhase.GENERATING_FIX_PLAN:                     return "12/16";
            case JobPhase.DETERMINING_RETRY_SCOPE:                 return "13/16";
            case JobPhase.VALIDATING_OUTPUT:                       return "14/16";
            case JobPhase.PUBLISHING:                              return "15/16";
            case JobPhase.COMPLETED:
            case JobPhase.FAILED:
            case JobPhase.CANCELLED:                               return "16/16";
            default:                                               return "-";
        }
    }

    private static string GetPhaseDisplayText(JobPhase phase, SceneReviewState? reviewState)
    {
        switch (phase)
        {
            case JobPhase.QUEUED:
                return "Queued...";
            case JobPhase.PREPARING_INPUT:
                return "Preparing input...";
            case JobPhase.ANALYZING_SCENE:
                return "Analyzing scene...";
            case JobPhase.AWAITING_SCENE_CONFIRMATION:
                if (reviewState == SceneReviewState.PROCESSING_FEEDBACK)
                {
                    return "Applying feedback...";
                }
                return "Awaiting scene confirmation...";
            case JobPhase.PLANNING_INTERACTIONS:
                return "Planning interactions...";
            case JobPhase.GENERATING_SPECS_INTERACTION_ELEMENTS:
                return "Generating interaction elements...";
            case JobPhase.GENERATING_SPECS_VISUALIZATION_ELEMENTS:
                return "Generating visualization elements...";
            case JobPhase.GENERATING_SPECS_STATES:
                return "Generating states...";
            case JobPhase.GENERATING_SPECS_TRANSITIONS:
                return "Generating transitions...";
            case JobPhase.GENERATING_SPECS:
                return "Generating specification...";
            case JobPhase.REVIEWING_CONSISTENCY:
                return "Reviewing consistency...";
            case JobPhase.GENERATING_FIX_PLAN:
                return "Generating fix plan...";
            case JobPhase.DETERMINING_RETRY_SCOPE:
                return "Determining retry scope...";
            case JobPhase.VALIDATING_OUTPUT:
                return "Validating output...";
            case JobPhase.PUBLISHING:
                return "Publishing...";
            case JobPhase.COMPLETED:
                return "Completed.";
            case JobPhase.FAILED:
                return "Failed.";
            case JobPhase.CANCELLED:
                return "Cancelled.";
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

    private static void CopyScreensToGroupDir(string sourceDir, string groupPath)
    {
        string targetDir = Path.Combine(groupPath, "screens");
        Directory.CreateDirectory(targetDir);
        foreach (string file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            string relative = file.Substring(sourceDir.Length)
                .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string dest = Path.Combine(targetDir, relative);
            string destDir = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(destDir))
                Directory.CreateDirectory(destDir);
            File.Copy(file, dest, overwrite: true);
        }
    }

    /// <summary>
    /// Clears chat/summary state when a new generation starts.
    /// </summary>
    private void ResetSceneConfirmationForNewGeneration()
    {
        _sceneSummaryText = string.Empty;
        _sceneReviewState = null;
        _hasSceneReviewPayload = false;
        _sceneReviewRevision = 0;
        _hasSceneReviewRevision = false;
        _sceneReviewUpdatedAt = null;
        _sceneDecisionLocked = false;
        _sceneDecisionAcceptedRevision = 0;
        _sceneConfirmedForCurrentJob = false;
        _sceneReviewMessage = string.Empty;
        _isSceneDecisionInFlight = false;
        _isSceneReviewPollInFlight = false;
        _chatMessages.Clear();
        _userChatInput = string.Empty;
        _chatScroll = Vector2.zero;
        _interactionPlanData = null;
        _foldInteractionElements = false;
        _foldVisualizationElements = false;
        _foldStates = false;
        _foldTransitions = false;
        _foldChatHistory = false;
        _foldReasoning = true;
    }

    /// <summary>
    /// Applies latest scene-review payload to local UI state.
    /// </summary>
    private void ApplySceneReviewResponse(SceneReviewResponse response)
    {
        if (response == null)
        {
            return;
        }

        _sceneReviewState = response.ReviewState;
        if (!string.IsNullOrWhiteSpace(response.Error))
        {
            _sceneReviewMessage = response.Error;
        }

        if (response.SceneReview == null)
        {
            _hasSceneReviewPayload = false;
            _hasSceneReviewRevision = false;
            _sceneSummaryText = string.Empty;
            _sceneReviewUpdatedAt = null;
            return;
        }

        _hasSceneReviewPayload = true;
        _sceneSummaryText = response.SceneReview.Summary ?? string.Empty;
        _sceneReviewUpdatedAt = response.SceneReview.UpdatedAt;

        _interactionPlanData = null;
        if (response.SceneReview.InteractionPlan != null)
        {
            try
            {
                var jObj = new JObject();
                foreach (var kvp in response.SceneReview.InteractionPlan)
                    jObj[kvp.Key] = kvp.Value;
                _interactionPlanData = jObj.ToObject<InteractionPlanData>();
            }
            catch (Exception ex)
            {
                Debug.LogWarning("Failed to parse interaction plan: " + ex.Message);
            }
        }

        int nextRevision = response.SceneReview.Revision;
        bool isRevisionIncremented = _hasSceneReviewRevision && nextRevision > _sceneReviewRevision;
        _sceneReviewRevision = nextRevision;
        _hasSceneReviewRevision = true;

        if (_sceneDecisionLocked &&
            !_sceneConfirmedForCurrentJob &&
            _sceneReviewState == SceneReviewState.PENDING &&
            nextRevision > _sceneDecisionAcceptedRevision)
        {
            _sceneDecisionLocked = false;
            _sceneReviewMessage = "Updated review revision received.";
        }

        if (isRevisionIncremented && string.IsNullOrWhiteSpace(_sceneReviewMessage))
        {
            _sceneReviewMessage = "Scene review updated to revision " + nextRevision + ".";
        }

        if (response.Status == JobStatus.CANCELLED || response.Phase == JobPhase.CANCELLED)
        {
            CloseSceneReviewUi();
        }
    }

    private void CloseSceneReviewUi()
    {
        _sceneSummaryText = string.Empty;
        _sceneReviewState = null;
        _hasSceneReviewPayload = false;
        _sceneReviewRevision = 0;
        _hasSceneReviewRevision = false;
        _sceneReviewUpdatedAt = null;
        _sceneDecisionLocked = false;
        _sceneDecisionAcceptedRevision = 0;
        _sceneConfirmedForCurrentJob = false;
        _sceneReviewMessage = string.Empty;
        _chatMessages.Clear();
        _userChatInput = string.Empty;
        _interactionPlanData = null;
        _foldInteractionElements = false;
        _foldVisualizationElements = false;
        _foldStates = false;
        _foldTransitions = false;
        _foldChatHistory = false;
        _foldReasoning = true;
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

        VivianBackendWindow[] windows = UnityEngine.Resources.FindObjectsOfTypeAll<VivianBackendWindow>();
        if (windows == null || windows.Length == 0)
        {
            return false;
        }

        windows[0].AddAgentResponse(text);
        return true;
    }
}
#endif
