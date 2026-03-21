#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using Vivian.Backend.Client;
using Vivian.Backend.Dtos;
using Vivian.Editor.Models;

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
    private const double SceneReviewPollIntervalSeconds = 1d;

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
    private string _screensDir = string.Empty;

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
    private SceneReviewState? _sceneReviewState;
    private bool _hasSceneReviewPayload;
    private int _sceneReviewRevision;
    private bool _hasSceneReviewRevision;
    private DateTimeOffset? _sceneReviewUpdatedAt;
    private bool _sceneDecisionLocked;
    private int _sceneDecisionAcceptedRevision;
    private bool _sceneConfirmedForCurrentJob;
    private string _sceneReviewMessage = string.Empty;

    private InteractionPlanData _interactionPlanData;
    private bool _foldInteractionElements;
    private bool _foldVisualizationElements;
    private bool _foldStates;
    private bool _foldTransitions;
    private bool _foldChatHistory;
    private bool _foldReasoning;

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
    private bool _isSceneReviewPollInFlight;
    private bool _isSceneDecisionInFlight;
    private bool _isStartingJob;
    private bool _isCancellingJob;
    private bool _isTestingConnectivity;
    private bool _isFetchingResult;
    private bool _resultFetchedForCurrentJob;

    private double _nextStatusPollAt;
    private double _nextLogsPollAt;
    private double _nextSceneReviewPollAt;

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
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Screens Directory", EditorStyles.boldLabel);
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField(string.IsNullOrEmpty(_screensDir) ? "(none)" : _screensDir, EditorStyles.wordWrappedLabel);
        if (GUILayout.Button("Browse…", GUILayout.Width(70)))
        {
            string picked = EditorUtility.OpenFolderPanel("Select Screens Directory", _screensDir, "");
            if (!string.IsNullOrEmpty(picked))
                _screensDir = picked;
        }
        if (!string.IsNullOrEmpty(_screensDir) && GUILayout.Button("Clear", GUILayout.Width(50)))
            _screensDir = string.Empty;
        EditorGUILayout.EndHorizontal();
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
    /// Renders scene review state from backend API payloads and submits revision-bound decisions.
    /// </summary>
    private void DrawSceneConfirmationSection()
    {
        EditorGUILayout.LabelField("Interaction Plan Review", EditorStyles.boldLabel);

        JobStatusResponse status = _jobService != null ? _jobService.LastKnownStatus : null;
        bool hasJob = _jobService != null && _jobService.HasJob;
        bool isAwaitingSceneConfirmation = status != null && status.Phase == JobPhase.AWAITING_SCENE_CONFIRMATION;
        bool isPendingReview = isAwaitingSceneConfirmation &&
                               _sceneReviewState == SceneReviewState.PENDING &&
                               _hasSceneReviewPayload;
        bool isProcessingFeedback = isAwaitingSceneConfirmation &&
                                    _sceneReviewState == SceneReviewState.PROCESSING_FEEDBACK;

        if (_hasSceneReviewRevision)
        {
            EditorGUILayout.LabelField("Revision", _sceneReviewRevision.ToString());
        }
        if (_sceneReviewUpdatedAt.HasValue)
        {
            EditorGUILayout.LabelField("Updated", _sceneReviewUpdatedAt.Value.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss"));
        }

        if (!string.IsNullOrWhiteSpace(_sceneReviewMessage))
        {
            EditorGUILayout.HelpBox(_sceneReviewMessage, MessageType.Info);
        }

        if (!hasJob)
        {
            EditorGUILayout.HelpBox("Start a run first. Scene review appears automatically when the backend requests confirmation.", MessageType.Info);
            return;
        }

        if (isProcessingFeedback)
        {
            EditorGUILayout.HelpBox("Applying feedback...", MessageType.Info);
            return;
        }
        else if (!isPendingReview)
        {
            string waitingMessage = isAwaitingSceneConfirmation
                ? "Waiting for pending scene review payload..."
                : "Scene review UI is shown only while the backend is awaiting scene confirmation.";
            EditorGUILayout.HelpBox(waitingMessage, MessageType.None);
            return;
        }

        _chatScroll = EditorGUILayout.BeginScrollView(_chatScroll, GUILayout.MinHeight(220), GUILayout.MaxHeight(500));

        if (_interactionPlanData != null)
        {
            DrawInteractionPlanFoldouts();
        }
        else
        {
            string summaryText = string.IsNullOrWhiteSpace(_sceneSummaryText) ? "(empty summary)" : _sceneSummaryText;
            var bubbleStyle = new GUIStyle(EditorStyles.helpBox)
            {
                wordWrap = true,
                padding = new RectOffset(10, 10, 8, 8)
            };
            float maxBubbleWidth = Mathf.Max(200f, EditorGUIUtility.currentViewWidth * 0.62f);
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(summaryText, bubbleStyle, GUILayout.MaxWidth(maxBubbleWidth));
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space(4);
        }

        DrawChatHistoryFoldout();

        EditorGUILayout.EndScrollView();

        bool disableActions = _isSceneDecisionInFlight || _sceneDecisionLocked || _sceneConfirmedForCurrentJob;

        EditorGUILayout.BeginHorizontal();
        _userChatInput = EditorGUILayout.TextField(_userChatInput, GUILayout.ExpandWidth(true));
        EditorGUI.BeginDisabledGroup(disableActions);
        if (GUILayout.Button(_isSceneDecisionInFlight ? "Sending..." : "Send Feedback", GUILayout.Width(120)))
        {
            string feedback = _userChatInput == null ? string.Empty : _userChatInput.Trim();
            if (string.IsNullOrWhiteSpace(feedback))
            {
                _sceneReviewMessage = "Feedback text is required when sending corrections.";
            }
            else
            {
                OnUserMessageSubmitted(feedback);
                _ = SubmitSceneReviewDecisionAsync(confirmed: false, feedback);
            }
        }

        if (GUILayout.Button("Confirm", GUILayout.Width(90)))
        {
            string feedback = _userChatInput == null ? string.Empty : _userChatInput.Trim();
            if (!string.IsNullOrWhiteSpace(feedback))
            {
                OnUserMessageSubmitted(feedback);
            }

            _ = SubmitSceneReviewDecisionAsync(confirmed: true, feedback);
        }
        EditorGUI.EndDisabledGroup();
        EditorGUILayout.EndHorizontal();

        if (_sceneDecisionLocked && !_sceneConfirmedForCurrentJob)
        {
            EditorGUILayout.HelpBox("Feedback accepted. Waiting for next pending revision before enabling actions.", MessageType.Info);
        }
        if (_sceneConfirmedForCurrentJob)
        {
            EditorGUILayout.HelpBox("Scene confirmation submitted. Waiting for backend pipeline to continue.", MessageType.Info);
        }
    }

    private void DrawInteractionPlanFoldouts()
    {
        var interactionElements = new List<ElementRoleData>();
        var visualizationElements = new List<ElementRoleData>();
        foreach (var role in _interactionPlanData.ElementRoles)
        {
            if (role.Category == "visualization")
                visualizationElements.Add(role);
            else
                interactionElements.Add(role);
        }

        DrawElementRolesFoldout(ref _foldInteractionElements, "Interaction Elements", interactionElements);
        DrawElementRolesFoldout(ref _foldVisualizationElements, "Visualization Elements", visualizationElements);
        DrawPlannedStatesFoldout();
        DrawPlannedTransitionsFoldout();
        DrawReasoningFoldout();
    }

    private void DrawElementRolesFoldout(ref bool foldout, string title, List<ElementRoleData> elements)
    {
        foldout = EditorGUILayout.Foldout(foldout, title + " (" + elements.Count + ")", true);
        if (!foldout) return;

        EditorGUI.indentLevel++;
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("Object Name", EditorStyles.boldLabel, GUILayout.Width(180));
        EditorGUILayout.LabelField("Type", EditorStyles.boldLabel, GUILayout.Width(120));
        EditorGUILayout.LabelField("Rationale", EditorStyles.boldLabel);
        EditorGUILayout.EndHorizontal();

        foreach (var elem in elements)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(elem.ObjectName, GUILayout.Width(180));
            EditorGUILayout.LabelField(elem.FuncSpecType, GUILayout.Width(120));
            EditorGUILayout.LabelField(elem.Rationale);
            EditorGUILayout.EndHorizontal();
        }
        EditorGUI.indentLevel--;
    }

    private void DrawPlannedStatesFoldout()
    {
        int count = _interactionPlanData.PlannedStates.Count;
        _foldStates = EditorGUILayout.Foldout(_foldStates, "States (" + count + ")", true);
        if (!_foldStates) return;

        EditorGUI.indentLevel++;
        foreach (var state in _interactionPlanData.PlannedStates)
        {
            EditorGUILayout.LabelField(state.Name, EditorStyles.boldLabel);
            EditorGUILayout.LabelField(state.Description, EditorStyles.wordWrappedLabel);
            if (state.InvolvedElements != null && state.InvolvedElements.Count > 0)
            {
                EditorGUILayout.LabelField("Elements: " + string.Join(", ", state.InvolvedElements));
            }
            if (state.ScreenFiles != null && state.ScreenFiles.Count > 0)
            {
                EditorGUILayout.LabelField("Screens: " + string.Join(", ", state.ScreenFiles));
            }
            EditorGUILayout.Space(2);
        }
        EditorGUI.indentLevel--;
    }

    private void DrawPlannedTransitionsFoldout()
    {
        int count = _interactionPlanData.PlannedTransitions.Count;
        _foldTransitions = EditorGUILayout.Foldout(_foldTransitions, "Transitions (" + count + ")", true);
        if (!_foldTransitions) return;

        EditorGUI.indentLevel++;
        foreach (var tr in _interactionPlanData.PlannedTransitions)
        {
            string trigger = string.IsNullOrEmpty(tr.TriggerElement) ? "auto" : tr.TriggerElement;
            EditorGUILayout.LabelField(tr.SourceState + "  ->  " + tr.DestinationState);
            EditorGUI.indentLevel++;
            EditorGUILayout.LabelField("Trigger: " + trigger + " (" + tr.TriggerDescription + ")");
            if (tr.GuardHints != null && tr.GuardHints.Count > 0)
            {
                EditorGUILayout.LabelField("Guards: " + string.Join("; ", tr.GuardHints));
            }
            EditorGUI.indentLevel--;
            EditorGUILayout.Space(2);
        }
        EditorGUI.indentLevel--;
    }

    private void DrawReasoningFoldout()
    {
        if (string.IsNullOrWhiteSpace(_interactionPlanData.Reasoning)) return;

        _foldReasoning = EditorGUILayout.Foldout(_foldReasoning, "Reasoning", true);
        if (!_foldReasoning) return;

        EditorGUI.indentLevel++;
        EditorGUILayout.LabelField(_interactionPlanData.Reasoning, EditorStyles.wordWrappedLabel);
        EditorGUI.indentLevel--;
    }

    private void DrawChatHistoryFoldout()
    {
        if (_chatMessages.Count == 0) return;

        _foldChatHistory = EditorGUILayout.Foldout(_foldChatHistory, "Chat History (" + _chatMessages.Count + ")", true);
        if (!_foldChatHistory) return;

        var bubbleStyle = new GUIStyle(EditorStyles.helpBox)
        {
            wordWrap = true,
            padding = new RectOffset(10, 10, 8, 8)
        };
        float maxBubbleWidth = Mathf.Max(200f, EditorGUIUtility.currentViewWidth * 0.62f);

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
        string phase = status != null ? status.Phase.ToString() : "-";
        string progress = GetProgressText(status);
        string message = GetStatusMessage(status);

        EditorGUILayout.LabelField("Job ID", jobId);
        EditorGUILayout.LabelField("State", state);
        EditorGUILayout.LabelField("Phase", phase);
        EditorGUILayout.LabelField("Progress", progress);
        EditorGUILayout.LabelField("Message", message);

        if (!string.IsNullOrWhiteSpace(_lastError))
        {
            EditorGUILayout.HelpBox(_lastError, MessageType.Error);
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

        JobStatusResponse currentStatus = _jobService?.LastKnownStatus;
        if (currentStatus?.Phase == JobPhase.VALIDATING_OUTPUT
            || currentStatus?.Phase == JobPhase.REVIEWING_CONSISTENCY)
        {
            EditorGUILayout.HelpBox(
                "The validator is running in the background. Live logs are not available during this phase.",
                MessageType.Info);
        }

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
            UseMockSceneAnalysis = _useMockSceneAnalysis,
            InteractionDescription = string.IsNullOrWhiteSpace(_interactionDescription) ? null : _interactionDescription,
            ScreensDir = string.IsNullOrWhiteSpace(_screensDir) ? null : _screensDir
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

            if (!string.IsNullOrWhiteSpace(_screensDir))
            {
                _statusMessage = "Copying screens...";
                CopyScreensToGroupDir(_screensDir, _groupPath);
                AssetDatabase.Refresh();
            }

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
        if (status.Status == JobStatus.CANCELLED)
        {
            _statusMessage = "Cancelled.";
            CloseSceneReviewUi();
        }

        if (status.Status == JobStatus.FAILED && !string.IsNullOrWhiteSpace(status.Error))
        {
            _lastError = "Job failed: " + ShortError(status.Error);
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
            ? ShortError(failedStatus.Error)
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
        int tracebackIndex = error.IndexOf("\nTraceback ", System.StringComparison.Ordinal);
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
            case JobPhase.QUEUED:                                return "1/16";
            case JobPhase.PREPARING_INPUT:                       return "2/16";
            case JobPhase.ANALYZING_SCENE:                       return "3/16";
            case JobPhase.AWAITING_SCENE_CONFIRMATION:           return "4/16";
            case JobPhase.PLANNING_INTERACTIONS:                 return "5/16";
            case JobPhase.GENERATING_SPECS_INTERACTION_ELEMENTS: return "6/16";
            case JobPhase.GENERATING_SPECS_VISUALIZATION_ELEMENTS: return "7/16";
            case JobPhase.GENERATING_SPECS_STATES:               return "8/16";
            case JobPhase.GENERATING_SPECS_TRANSITIONS:          return "9/16";
            case JobPhase.GENERATING_SPECS:                      return "10/16";
            case JobPhase.REVIEWING_CONSISTENCY:                 return "11/16";
            case JobPhase.GENERATING_FIX_PLAN:                   return "12/16";
            case JobPhase.DETERMINING_RETRY_SCOPE:               return "13/16";
            case JobPhase.VALIDATING_OUTPUT:                     return "14/16";
            case JobPhase.PUBLISHING:                            return "15/16";
            case JobPhase.COMPLETED:
            case JobPhase.FAILED:
            case JobPhase.CANCELLED:                             return "16/16";
            default:                                 return "-";
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
        _foldReasoning = false;
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
        _foldReasoning = false;
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
