#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

public partial class GenerateInteractionsWindow : EditorWindow
{
    public struct GeneratedPaths
    {
        public string GroupPath;
        public string SceneJsonPath;
        public string ViewsManifestPath;
        public string SceneDir;
    }

    [MenuItem("Assets/Generate Interactions", false, 2000)]
    public static void ShowWindow()
    {
        Debug.LogWarning("Generate Interactions window has been merged into Vivian Backend. Open 'Vivian/Backend Job Window'.");
    }

    private enum Step
    {
        SelectObjects,
        DefineInteractionElements
    }

    private Step _currentStep = Step.SelectObjects;
    private Vector2 _scrollPos;
    private readonly Dictionary<GameObject, bool> _selection = new Dictionary<GameObject, bool>();
    private readonly List<GameObject> _selectedObjects = new List<GameObject>();
    private bool _showChildObjects = false;
    private string _groupName = string.Empty;
    private string _interactionDescription = string.Empty;
    private bool _startVivianPipeline = true;
    private bool _onlySceneAnalysis = false;
    private bool _useMockSceneAnalysis = false;
    private const int RenderWidth = 1024;
    private const int RenderHeight = 1024;
    private const float CameraFov = 45f;
    private const float PaddingFactor = 1.1f;
    private const float MinProjectionSizePx = 4f;
    private static readonly Color BackgroundColor = new Color(0.85f, 0.85f, 0.85f, 1f);
    private Camera _previewCamera;
    private RenderTexture _previewTexture;
    private Process _runningProc;
    private CancellationTokenSource _pythonCts;
    private readonly StringBuilder _liveLogBuffer = new StringBuilder();
    private DateTime _lastOutputAt = DateTime.MinValue;
    private string _groupPath = string.Empty;
    private string _sceneSummaryText = string.Empty;
    private string _sceneFeedbackText = string.Empty;
    private DateTime _sceneSummaryLastWrite = DateTime.MinValue;
    private enum ChatRole
    {
        Agent,
        User
    }

    private struct ChatMessage
    {
        public ChatRole role;
        public string text;
    }

    private readonly List<ChatMessage> _chatMessages = new List<ChatMessage>();
    private Vector2 _chatScroll;
    private string _userChatInput = string.Empty;
    private bool _showAdvanced;

    private const string ViewsFolderName = "views";
    private static readonly ViewDirection[] ViewDirections =
    {
        new ViewDirection("front", Vector3.back),
        new ViewDirection("back", Vector3.forward),
        new ViewDirection("left", Vector3.left),
        new ViewDirection("right", Vector3.right),
        new ViewDirection("top", Vector3.up),
        new ViewDirection("bottom", Vector3.down),
        new ViewDirection("iso_top_left", new Vector3(-1f, 1f, 1f).normalized),
        new ViewDirection("iso_top_right", new Vector3(1f, 1f, 1f).normalized)
    };
    private static readonly Regex SafeNameRegex = new Regex("[^A-Za-z0-9_-]", RegexOptions.Compiled);

    public static bool TryBuildGeneratedPaths(string groupName, out GeneratedPaths paths)
    {
        paths = default;
        string trimmedGroup = string.IsNullOrWhiteSpace(groupName) ? string.Empty : groupName.Trim();
        if (string.IsNullOrEmpty(trimmedGroup))
        {
            return false;
        }

        string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        string basePath = Path.Combine(projectRoot, "Packages", "vivian-example-prototypes", "Resources");
        string groupPath = Path.Combine(basePath, trimmedGroup);

        paths = new GeneratedPaths
        {
            GroupPath = groupPath,
            SceneJsonPath = Path.Combine(groupPath, "scene.json"),
            ViewsManifestPath = Path.Combine(groupPath, "views_manifest.json"),
            SceneDir = groupPath
        };

        return true;
    }

    public static bool TryGenerateInteractionAssets(
        IReadOnlyList<GameObject> selectedObjects,
        string groupName,
        string interactionDescription,
        bool startPipeline,
        bool onlySceneAnalysis,
        bool useMockSceneAnalysis,
        out GeneratedPaths paths,
        out string error)
    {
        paths = default;
        error = string.Empty;

        if (!TryBuildGeneratedPaths(groupName, out GeneratedPaths predictedPaths))
        {
            error = "Group name is required.";
            return false;
        }

        if (selectedObjects == null || selectedObjects.Count == 0)
        {
            error = "At least one GameObject must be selected.";
            return false;
        }

        var validSelection = new List<GameObject>();
        for (int i = 0; i < selectedObjects.Count; i++)
        {
            if (selectedObjects[i] != null)
            {
                validSelection.Add(selectedObjects[i]);
            }
        }

        if (validSelection.Count == 0)
        {
            error = "Selected GameObjects are no longer valid.";
            return false;
        }

        var worker = CreateInstance<GenerateInteractionsWindow>();
        try
        {
            worker._groupName = groupName.Trim();
            worker._interactionDescription = interactionDescription ?? string.Empty;
            worker._startVivianPipeline = startPipeline;
            worker._onlySceneAnalysis = onlySceneAnalysis;
            worker._useMockSceneAnalysis = useMockSceneAnalysis;

            worker._selection.Clear();
            worker._selectedObjects.Clear();

            foreach (GameObject go in validSelection)
            {
                worker._selection[go] = true;
                worker._selectedObjects.Add(go);
            }

            worker.CreateInteractionObjects();

            paths = predictedPaths;
            if (!File.Exists(paths.SceneJsonPath))
            {
                error = "scene.json was not generated at: " + paths.SceneJsonPath;
                return false;
            }

            if (!File.Exists(paths.ViewsManifestPath))
            {
                error = "views_manifest.json was not generated at: " + paths.ViewsManifestPath;
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            DestroyImmediate(worker);
        }
    }

    private void OnGUI()
    {
        EditorGUILayout.HelpBox(
            "This window has been merged into Vivian Backend.\nOpen 'Vivian/Backend Job Window' to run the full workflow.",
            MessageType.Info);

        if (GUILayout.Button("Open Vivian Backend Window"))
        {
            EditorApplication.ExecuteMenuItem("Vivian/Backend Job Window");
        }
    }

    private void OnUserMessageSubmitted(string text)
    {
        _chatMessages.Add(new ChatMessage { role = ChatRole.User, text = text });
        _userChatInput = string.Empty;
        _chatScroll.y = float.MaxValue;
        GUI.FocusControl(null);
    }

    public void AddAgentResponse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        _chatMessages.Add(new ChatMessage { role = ChatRole.Agent, text = text.Trim() });
        _chatScroll.y = float.MaxValue;
        Repaint();
    }

    public static bool TryAddAgentResponse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var windows = Resources.FindObjectsOfTypeAll<GenerateInteractionsWindow>();
        if (windows == null || windows.Length == 0)
        {
            return false;
        }

        windows[0].AddAgentResponse(text);
        return true;
    }
}
#endif
