#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Vivian.Backend.Client;
using Vivian.Backend.Dtos;
using Vivian.Editor.Models;

/// <summary>
/// Unified editor window for the Vivian backend workflow.
/// Split into partial classes:
///   .Stepper.cs    - Pipeline stepper visualization, status bar, spinner, ThemeColors
///   .Sections.cs   - Collapsible foldout sections (Advanced Settings, Scope, Interaction, Logs, Result)
///   .SceneReview.cs - Scene confirmation UI with chat and interaction plan review
///   .Backend.cs    - All async backend operations (start, cancel, polling, fetch, connectivity)
///   .Events.cs     - Job service event subscriptions and handlers
///   .Helpers.cs    - Utility methods, selection management, path sync, chat
/// </summary>
public sealed partial class VivianBackendWindow : EditorWindow
{
    private const string ServerUrlEditorPrefKey = "VivianBackend.ServerUrl";
    private const string DefaultServerUrl = "http://127.0.0.1:8000";

    private const double StatusPollIntervalSeconds = 0.5;
    private const double LogsPollIntervalSeconds = 0.75d;
    private const double SceneReviewPollIntervalSeconds = 1d;

    private VivianApiClient _apiClient;
    private VivianJobService _jobService;

    // Configuration fields
    private string _serverUrl = DefaultServerUrl;
    private string _sceneJsonPath = string.Empty;
    private string _viewsManifestPath = string.Empty;
    private string _sceneDir = string.Empty;
    private bool _startPipeline = true;
    private bool _onlySceneAnalysis;
    private bool _useMockSceneAnalysis;

    // Scene selection
    private readonly Dictionary<GameObject, bool> _selection = new Dictionary<GameObject, bool>();
    private readonly List<GameObject> _selectedObjects = new List<GameObject>();
    private Vector2 _selectionScroll;
    private bool _showChildObjects;
    private string _groupName = string.Empty;
    private string _interactionDescription = string.Empty;
    private string _groupPath = string.Empty;
    private string _screensDir = string.Empty;

    // Chat / Scene review
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

    // Interaction plan review foldouts
    private InteractionPlanData _interactionPlanData;
    private bool _foldInteractionElements;
    private bool _foldVisualizationElements;
    private bool _foldStates;
    private bool _foldTransitions;
    private bool _foldChatHistory;
    private bool _foldReasoning = true;

    // Status and output
    private string _statusMessage = "Idle";
    private string _lastError = string.Empty;
    private string _cancelMessage = string.Empty;
    private string _logsText = string.Empty;
    private string _resultText = string.Empty;

    // Connectivity
    private string _connectivityMessage = string.Empty;
    private MessageType _connectivityMessageType = MessageType.None;

    // Scroll positions
    private Vector2 _mainScroll;
    private Vector2 _logsScroll;
    private Vector2 _resultScroll;
    private bool _scrollLogsToEnd;

    // Polling and in-flight flags
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

    // Polling timing
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
        minSize = new Vector2(520, 450);
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

        // Fixed header: Pipeline stepper + Status bar (outside scroll view)
        DrawPipelineStepper();
        DrawStatusBar();
        DrawErrorBar();

        // Scrollable content area
        _mainScroll = EditorGUILayout.BeginScrollView(_mainScroll);

        EditorGUILayout.Space(2);
        DrawAdvancedSettingsSection();
        DrawScopeSelectionFoldout();
        DrawInteractionSetupFoldout();
        DrawSceneConfirmationSection();
        DrawLogsFoldout();
        DrawResultFoldout();

        EditorGUILayout.EndScrollView();
    }
}
#endif
