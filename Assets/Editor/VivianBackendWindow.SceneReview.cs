#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Vivian.Backend.Dtos;
using Vivian.Editor.Models;

public sealed partial class VivianBackendWindow
{
    /// <summary>
    /// Renders scene review with "Action Required" banner. Only visible during AWAITING_SCENE_CONFIRMATION.
    /// </summary>
    private void DrawSceneConfirmationSection()
    {
        JobStatusResponse status = _jobService != null ? _jobService.LastKnownStatus : null;
        bool hasJob = _jobService != null && _jobService.HasJob;
        bool isAwaitingSceneConfirmation = status != null && status.Phase == JobPhase.AWAITING_SCENE_CONFIRMATION;
        bool isPendingReview = isAwaitingSceneConfirmation &&
                               _sceneReviewState == SceneReviewState.PENDING &&
                               _hasSceneReviewPayload;
        bool isProcessingFeedback = isAwaitingSceneConfirmation &&
                                    _sceneReviewState == SceneReviewState.PROCESSING_FEEDBACK;

        // Only show this section during scene confirmation phase
        if (!hasJob || !isAwaitingSceneConfirmation)
            return;

        // "Action Required" banner
        Rect bannerRect = GUILayoutUtility.GetRect(0, 28, GUILayout.ExpandWidth(true));
        EditorGUI.DrawRect(bannerRect, ThemeColors.ActionRequired);
        GUIStyle bannerStyle = new GUIStyle(EditorStyles.boldLabel)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = 12
        };
        Color prevColor = GUI.color;
        GUI.color = Color.white;
        GUI.Label(bannerRect, "Action Required: Review and Confirm Scene Analysis", bannerStyle);
        GUI.color = prevColor;

        EditorGUILayout.Space(4);

        // Metadata
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

        if (isProcessingFeedback)
        {
            EditorGUILayout.HelpBox("Applying feedback...", MessageType.Info);
            return;
        }

        if (!isPendingReview)
        {
            EditorGUILayout.HelpBox("Waiting for pending scene review payload...", MessageType.None);
            return;
        }

        // Main review content
        _chatScroll = EditorGUILayout.BeginScrollView(_chatScroll, GUILayout.MinHeight(100), GUILayout.MaxHeight(400));

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

        // Action controls
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

        DrawSectionSeparator();
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
}
#endif
