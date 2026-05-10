#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using System.Collections.Generic;
using Vivian.Backend.Dtos;

public sealed partial class VivianBackendWindow
{
    // Foldout states for collapsible sections
    private bool _foldAdvancedSettings;
    private bool _foldScopeSelection = true;
    private bool _foldInteractionSetup = true;
    private bool _foldLogs;
    private bool _foldResult;

    /// <summary>
    /// Consolidated advanced settings: server URL, pipeline options, auto-filled paths.
    /// </summary>
    private void DrawAdvancedSettingsSection()
    {
        _foldAdvancedSettings = EditorGUILayout.Foldout(_foldAdvancedSettings, "Advanced Settings", true);
        if (!_foldAdvancedSettings) return;

        EditorGUI.indentLevel++;

        // Server URL
        EditorGUILayout.LabelField("Backend Server", EditorStyles.boldLabel);
        EditorGUILayout.BeginHorizontal();
        _serverUrl = EditorGUILayout.TextField("Server URL", _serverUrl);
        if (GUILayout.Button("Save", GUILayout.Width(60)))
        {
            SaveServerUrl();
        }
        EditorGUI.BeginDisabledGroup(_isTestingConnectivity);
        if (GUILayout.Button("Test", GUILayout.Width(60)))
        {
            TestConnectivityAsync();
        }
        EditorGUI.EndDisabledGroup();
        EditorGUILayout.EndHorizontal();

        if (_connectivityMessageType != MessageType.None && !string.IsNullOrEmpty(_connectivityMessage))
        {
            EditorGUILayout.HelpBox(_connectivityMessage, _connectivityMessageType);
        }

        EditorGUILayout.Space(2);

        // Pipeline Options
        EditorGUILayout.LabelField("Pipeline Options", EditorStyles.boldLabel);
        _startPipeline = EditorGUILayout.ToggleLeft("Start Pipeline", _startPipeline);
        EditorGUI.BeginDisabledGroup(!_startPipeline);
        _onlySceneAnalysis = EditorGUILayout.ToggleLeft("Only Scene Analysis", _onlySceneAnalysis);
        _useMockSceneAnalysis = EditorGUILayout.ToggleLeft("Use Mock Scene Analysis", _useMockSceneAnalysis);
        EditorGUI.EndDisabledGroup();

        EditorGUILayout.Space(2);

        // Auto-filled paths
        EditorGUILayout.LabelField("Auto-Filled Paths", EditorStyles.boldLabel);
        EditorGUI.BeginDisabledGroup(true);
        EditorGUILayout.TextField("group_path", _groupPath);
        EditorGUILayout.TextField("scene.json", _sceneJsonPath);
        EditorGUILayout.TextField("views_manifest.json", _viewsManifestPath);
        EditorGUILayout.TextField("scene dir", _sceneDir);
        EditorGUI.EndDisabledGroup();

        if (string.IsNullOrWhiteSpace(_groupName))
        {
            EditorGUILayout.HelpBox("Enter a group name to generate group_path.", MessageType.Info);
        }

        EditorGUI.indentLevel--;
        DrawSectionSeparator();
    }

    /// <summary>
    /// Scene object scope and object-level selection in a collapsible foldout.
    /// </summary>
    private void DrawScopeSelectionFoldout()
    {
        _foldScopeSelection = EditorGUILayout.Foldout(_foldScopeSelection, "Scope Selection", true);
        if (!_foldScopeSelection) return;

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

        _selectionScroll = EditorGUILayout.BeginScrollView(_selectionScroll, GUILayout.Height(200));

        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("Select", GUILayout.Width(50));
        EditorGUILayout.LabelField("GameObject");
        EditorGUILayout.LabelField("Active", GUILayout.Width(50));
        EditorGUILayout.EndHorizontal();

        foreach (GameObject go in allObjects)
        {
            if (go == null) continue;

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
        DrawSectionSeparator();
    }

    /// <summary>
    /// User inputs: group name, description, screens directory.
    /// </summary>
    private void DrawInteractionSetupFoldout()
    {
        _foldInteractionSetup = EditorGUILayout.Foldout(_foldInteractionSetup, "Interaction Setup", true);
        if (!_foldInteractionSetup) return;

        EditorGUI.BeginChangeCheck();
        string newGroupName = EditorGUILayout.TextField("Group Name", _groupName);
        if (EditorGUI.EndChangeCheck())
        {
            _groupName = newGroupName;
            SyncAutoFilledPaths();
        }

        EditorGUILayout.LabelField("Interaction Description");
        _interactionDescription = EditorGUILayout.TextArea(_interactionDescription, GUILayout.MinHeight(60));

        EditorGUILayout.Space(2);
        EditorGUILayout.LabelField("Screens Directory", EditorStyles.miniLabel);
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField(string.IsNullOrEmpty(_screensDir) ? "(none)" : _screensDir, EditorStyles.wordWrappedLabel);
        if (GUILayout.Button("Browse...", GUILayout.Width(70)))
        {
            string picked = EditorUtility.OpenFolderPanel("Select Screens Directory", _screensDir, "");
            if (!string.IsNullOrEmpty(picked))
                _screensDir = picked;
        }
        if (!string.IsNullOrEmpty(_screensDir) && GUILayout.Button("Clear", GUILayout.Width(50)))
            _screensDir = string.Empty;
        EditorGUILayout.EndHorizontal();

        DrawSectionSeparator();
    }

    /// <summary>
    /// Logs section in a collapsible foldout.
    /// </summary>
    private void DrawLogsFoldout()
    {
        EditorGUILayout.BeginHorizontal();
        _foldLogs = EditorGUILayout.Foldout(_foldLogs, "Logs", true);
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("Clear Logs", GUILayout.Width(80)))
        {
            _jobService.ClearLogs();
        }
        EditorGUILayout.EndHorizontal();

        if (!_foldLogs) return;

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

        _logsScroll = EditorGUILayout.BeginScrollView(_logsScroll, GUILayout.MinHeight(120), GUILayout.MaxHeight(300));
        EditorGUILayout.TextArea(string.IsNullOrEmpty(_logsText) ? "(no logs yet)" : _logsText, GUILayout.ExpandHeight(true));
        EditorGUILayout.EndScrollView();
    }

    /// <summary>
    /// Result section in a collapsible foldout.
    /// </summary>
    private void DrawResultFoldout()
    {
        EditorGUILayout.BeginHorizontal();
        _foldResult = EditorGUILayout.Foldout(_foldResult, "Result", true);
        GUILayout.FlexibleSpace();
        EditorGUI.BeginDisabledGroup(string.IsNullOrEmpty(_resultText));
        if (GUILayout.Button("Copy to Clipboard", GUILayout.Width(130)))
        {
            EditorGUIUtility.systemCopyBuffer = _resultText;
        }
        EditorGUI.EndDisabledGroup();
        EditorGUILayout.EndHorizontal();

        if (!_foldResult) return;

        _resultScroll = EditorGUILayout.BeginScrollView(_resultScroll, GUILayout.MinHeight(100), GUILayout.MaxHeight(400));
        EditorGUILayout.TextArea(string.IsNullOrEmpty(_resultText) ? "(no result yet)" : _resultText, GUILayout.ExpandHeight(true));
        EditorGUILayout.EndScrollView();
    }

    private void DrawSectionSeparator()
    {
        EditorGUILayout.Space(1);
        Rect sepRect = GUILayoutUtility.GetRect(0, 1, GUILayout.ExpandWidth(true));
        EditorGUI.DrawRect(sepRect, ThemeColors.SectionSeparator);
        EditorGUILayout.Space(1);
    }
}
#endif
