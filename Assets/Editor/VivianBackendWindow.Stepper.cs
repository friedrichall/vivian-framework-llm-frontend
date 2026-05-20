#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using Vivian.Backend.Dtos;

public sealed partial class VivianBackendWindow
{
    private struct PipelineStepGroup
    {
        public string Label;
        public JobPhase[] Phases;
        public bool RequiresUserAction;
    }

    private enum StepState
    {
        Pending,
        Active,
        Completed,
        Failed
    }

    private static readonly PipelineStepGroup[] StepGroups =
    {
        new PipelineStepGroup
        {
            Label = "Setup",
            Phases = new[] { JobPhase.QUEUED, JobPhase.PREPARING_INPUT }
        },
        new PipelineStepGroup
        {
            Label = "Analysis",
            Phases = new[] { JobPhase.ANALYZING_SCENE }
        },
        new PipelineStepGroup
        {
            Label = "Planning",
            Phases = new[] { JobPhase.PLANNING_INTERACTIONS }
        },
        new PipelineStepGroup
        {
            Label = "Review",
            Phases = new[] { JobPhase.AWAITING_SCENE_CONFIRMATION },
            RequiresUserAction = true
        },
        new PipelineStepGroup
        {
            Label = "Generation",
            Phases = new[]
            {
                JobPhase.GENERATING_SPECS_INTERACTION_ELEMENTS,
                JobPhase.GENERATING_SPECS_VISUALIZATION_ELEMENTS,
                JobPhase.GENERATING_SPECS_STATES,
                JobPhase.GENERATING_SPECS_TRANSITIONS,
                JobPhase.GENERATING_SPECS
            }
        },
        new PipelineStepGroup
        {
            Label = "Validation",
            Phases = new[]
            {
                JobPhase.REVIEWING_CONSISTENCY,
                JobPhase.VALIDATING_OUTPUT
            }
        },
        new PipelineStepGroup
        {
            Label = "Retry",
            Phases = new[]
            {
                JobPhase.GENERATING_FIX_PLAN,
                JobPhase.DETERMINING_RETRY_SCOPE
            }
        },
        new PipelineStepGroup
        {
            Label = "Publish",
            Phases = new[] { JobPhase.PUBLISHING, JobPhase.COMPLETED }
        }
    };

    private static class ThemeColors
    {
        public static Color StepCompleted => EditorGUIUtility.isProSkin
            ? new Color(0.3f, 0.78f, 0.35f)
            : new Color(0.18f, 0.6f, 0.22f);

        public static Color StepActive => EditorGUIUtility.isProSkin
            ? new Color(0.35f, 0.58f, 0.98f)
            : new Color(0.2f, 0.4f, 0.85f);

        public static Color StepPending => EditorGUIUtility.isProSkin
            ? new Color(0.45f, 0.45f, 0.45f, 0.5f)
            : new Color(0.6f, 0.6f, 0.6f, 0.5f);

        public static Color StepFailed => EditorGUIUtility.isProSkin
            ? new Color(0.95f, 0.3f, 0.3f)
            : new Color(0.8f, 0.2f, 0.2f);

        public static Color ConnectorDone => StepCompleted;
        public static Color ConnectorPending => StepPending;

        public static Color ActionRequired => EditorGUIUtility.isProSkin
            ? new Color(0.95f, 0.7f, 0.2f)
            : new Color(0.85f, 0.55f, 0.1f);

        public static Color StatusBarBg => EditorGUIUtility.isProSkin
            ? new Color(0.22f, 0.22f, 0.22f)
            : new Color(0.82f, 0.82f, 0.82f);

        public static Color SectionSeparator => EditorGUIUtility.isProSkin
            ? new Color(0.18f, 0.18f, 0.18f)
            : new Color(0.72f, 0.72f, 0.72f);
    }

    // Spinner state
    private int _spinnerFrame;
    private double _nextSpinnerUpdateAt;
    private const double SpinnerIntervalSeconds = 0.08;
    private const float StepperHeight = 70f;
    private const float StatusBarHeight = 28f;

    private StepState GetStepState(int groupIndex, JobPhase? currentPhase, JobStatus? status)
    {
        if (!status.HasValue)
            return StepState.Pending;

        // Terminal SUCCEEDED: the backend may report SUCCEEDED while the last
        // known phase still trails behind (e.g. VALIDATING_OUTPUT or PUBLISHING)
        // because the logs poll can update status without updating phase. The
        // job is done, so every group must read as Completed regardless of phase.
        if (status.Value == JobStatus.SUCCEEDED)
            return StepState.Completed;

        if (!currentPhase.HasValue)
            return StepState.Pending;

        if (status.Value == JobStatus.FAILED)
        {
            // Find which group the failure occurred in
            for (int i = 0; i < StepGroups.Length; i++)
            {
                if (ContainsPhase(StepGroups[i].Phases, currentPhase.Value))
                {
                    if (i == groupIndex)
                        return StepState.Failed;
                    if (i > groupIndex)
                        return StepState.Completed;
                    return StepState.Pending;
                }
            }
            return StepState.Pending;
        }

        if (status.Value == JobStatus.CANCELLED)
        {
            for (int i = 0; i < StepGroups.Length; i++)
            {
                if (ContainsPhase(StepGroups[i].Phases, currentPhase.Value))
                {
                    if (i < groupIndex)
                        return StepState.Completed;
                    return StepState.Pending;
                }
            }
            return StepState.Pending;
        }

        // COMPLETED terminal phase maps to the last group
        if (currentPhase.Value == JobPhase.COMPLETED || currentPhase.Value == JobPhase.CANCELLED || currentPhase.Value == JobPhase.FAILED)
        {
            if (currentPhase.Value == JobPhase.COMPLETED)
                return StepState.Completed;
            return StepState.Pending;
        }

        // Find the current group index
        int currentGroupIndex = -1;
        for (int i = 0; i < StepGroups.Length; i++)
        {
            if (ContainsPhase(StepGroups[i].Phases, currentPhase.Value))
            {
                currentGroupIndex = i;
                break;
            }
        }

        if (currentGroupIndex < 0)
            return StepState.Pending;

        if (groupIndex < currentGroupIndex)
            return StepState.Completed;
        if (groupIndex == currentGroupIndex)
            return StepState.Active;
        return StepState.Pending;
    }

    private static bool ContainsPhase(JobPhase[] phases, JobPhase target)
    {
        for (int i = 0; i < phases.Length; i++)
        {
            if (phases[i] == target)
                return true;
        }
        return false;
    }

    /// <summary>
    /// For multi-phase groups, returns "2/5" style sub-progress text. Null if single-phase or not active.
    /// </summary>
    private string GetSubProgress(int groupIndex, JobPhase? currentPhase)
    {
        if (!currentPhase.HasValue)
            return null;

        var group = StepGroups[groupIndex];
        if (group.Phases.Length <= 1)
            return null;

        for (int i = 0; i < group.Phases.Length; i++)
        {
            if (group.Phases[i] == currentPhase.Value)
                return (i + 1) + "/" + group.Phases.Length;
        }
        return null;
    }

    private void DrawPipelineStepper()
    {
        // Reserve fixed rect for the stepper
        Rect stepperRect = GUILayoutUtility.GetRect(0, StepperHeight, GUILayout.ExpandWidth(true));

        // Draw subtle background
        Color bgColor = EditorGUIUtility.isProSkin
            ? new Color(0.19f, 0.19f, 0.19f)
            : new Color(0.85f, 0.85f, 0.85f);
        EditorGUI.DrawRect(stepperRect, bgColor);

        JobStatusResponse status = _jobService?.LastKnownStatus;
        JobPhase? currentPhase = status?.Phase;
        JobStatus? currentStatus = status?.Status;

        float padding = 20f;
        float usableWidth = stepperRect.width - padding * 2;
        float stepWidth = usableWidth / StepGroups.Length;
        float circleY = stepperRect.y + 18f;
        float circleRadius = 10f;
        float labelY = circleY + circleRadius + 6f;

        // Centered label style
        GUIStyle labelStyle = new GUIStyle(EditorStyles.miniLabel)
        {
            alignment = TextAnchor.UpperCenter,
            fontSize = 10,
            wordWrap = false
        };

        GUIStyle subProgressStyle = new GUIStyle(EditorStyles.miniLabel)
        {
            alignment = TextAnchor.UpperCenter,
            fontSize = 9
        };

        for (int i = 0; i < StepGroups.Length; i++)
        {
            float centerX = stepperRect.x + padding + stepWidth * i + stepWidth * 0.5f;
            StepState state = GetStepState(i, currentPhase, currentStatus);

            // Draw connector line to next step
            if (i < StepGroups.Length - 1)
            {
                float nextCenterX = stepperRect.x + padding + stepWidth * (i + 1) + stepWidth * 0.5f;
                float lineY = circleY;
                Color lineColor = (state == StepState.Completed)
                    ? ThemeColors.ConnectorDone
                    : ThemeColors.ConnectorPending;
                EditorGUI.DrawRect(new Rect(centerX + circleRadius + 2, lineY - 1, nextCenterX - centerX - circleRadius * 2 - 4, 2), lineColor);
            }

            // Draw circle background
            Color circleColor;
            switch (state)
            {
                case StepState.Completed:
                    circleColor = ThemeColors.StepCompleted;
                    break;
                case StepState.Active:
                    // Pulse effect
                    float pulse = 0.85f + 0.15f * Mathf.Sin((float)EditorApplication.timeSinceStartup * 3f);
                    Color activeBase = ThemeColors.StepActive;
                    circleColor = new Color(activeBase.r, activeBase.g, activeBase.b, pulse);
                    break;
                case StepState.Failed:
                    circleColor = ThemeColors.StepFailed;
                    break;
                default:
                    circleColor = ThemeColors.StepPending;
                    break;
            }

            Rect circleRect = new Rect(centerX - circleRadius, circleY - circleRadius, circleRadius * 2, circleRadius * 2);
            DrawFilledCircle(circleRect, circleColor);

            // Draw icon inside circle
            GUIContent iconContent = null;
            switch (state)
            {
                case StepState.Completed:
                    iconContent = EditorGUIUtility.IconContent("TestPassed");
                    break;
                case StepState.Failed:
                    iconContent = EditorGUIUtility.IconContent("TestFailed");
                    break;
                case StepState.Active:
                    // Show spinner icon for active step
                    string spinIcon = "WaitSpin" + _spinnerFrame.ToString("D2");
                    iconContent = EditorGUIUtility.IconContent(spinIcon);
                    break;
                default:
                    iconContent = EditorGUIUtility.IconContent("TestNormal");
                    break;
            }

            if (iconContent != null && iconContent.image != null)
            {
                Rect iconRect = new Rect(centerX - 7, circleY - 7, 14, 14);
                Color prevIconColor = GUI.color;
                GUI.color = Color.white;
                GUI.DrawTexture(iconRect, iconContent.image, ScaleMode.ScaleToFit);
                GUI.color = prevIconColor;
            }

            // Draw label
            Color prevColor = GUI.color;
            if (state == StepState.Pending)
                GUI.color = new Color(prevColor.r, prevColor.g, prevColor.b, 0.5f);
            else if (state == StepState.Active)
                GUI.color = ThemeColors.StepActive;
            else if (state == StepState.Failed)
                GUI.color = ThemeColors.StepFailed;

            string label = StepGroups[i].Label;
            Rect labelRect = new Rect(centerX - stepWidth * 0.5f, labelY, stepWidth, 14);
            GUI.Label(labelRect, label, labelStyle);
            GUI.color = prevColor;

            // Sub-progress for multi-phase groups
            if (state == StepState.Active)
            {
                string subProg = GetSubProgress(i, currentPhase);
                if (subProg != null)
                {
                    Color prevColor2 = GUI.color;
                    GUI.color = ThemeColors.StepActive;
                    Rect subRect = new Rect(centerX - stepWidth * 0.5f, labelY + 12, stepWidth, 12);
                    GUI.Label(subRect, subProg, subProgressStyle);
                    GUI.color = prevColor2;
                }
            }

            // "Action Required" indicator for Review step when active
            if (StepGroups[i].RequiresUserAction && state == StepState.Active)
            {
                Color prevColor3 = GUI.color;
                GUI.color = ThemeColors.ActionRequired;
                Rect actionRect = new Rect(centerX - 4, circleY - circleRadius - 10, 8, 8);
                EditorGUI.DrawRect(actionRect, ThemeColors.ActionRequired);
                GUI.color = prevColor3;
            }
        }

        // Bottom separator
        EditorGUI.DrawRect(new Rect(stepperRect.x, stepperRect.yMax - 1, stepperRect.width, 1), ThemeColors.SectionSeparator);
    }

    /// <summary>
    /// Draws a filled circle approximation using EditorGUI.DrawRect with multiple rects.
    /// </summary>
    private static void DrawFilledCircle(Rect bounds, Color color)
    {
        float cx = bounds.x + bounds.width * 0.5f;
        float cy = bounds.y + bounds.height * 0.5f;
        float r = bounds.width * 0.5f;

        // Approximate circle with horizontal slices
        int slices = Mathf.Max(6, (int)(r * 2));
        for (int i = 0; i < slices; i++)
        {
            float y = -r + (2f * r * i / (slices - 1));
            float halfWidth = Mathf.Sqrt(Mathf.Max(0, r * r - y * y));
            if (halfWidth < 0.5f) continue;
            EditorGUI.DrawRect(new Rect(cx - halfWidth, cy + y, halfWidth * 2, 2f * r / slices + 0.5f), color);
        }
    }

    private void DrawStatusBar()
    {
        Rect barRect = GUILayoutUtility.GetRect(0, StatusBarHeight, GUILayout.ExpandWidth(true));
        EditorGUI.DrawRect(barRect, ThemeColors.StatusBarBg);

        JobStatusResponse status = _jobService?.LastKnownStatus;
        bool isJobActive = _jobService != null && _jobService.IsJobActive;

        // Layout within the bar
        float x = barRect.x + 8;
        float midY = barRect.y + (barRect.height - 18) * 0.5f;

        // Spinner (only when active)
        if (isJobActive)
        {
            UpdateSpinnerFrame();
            string spinIcon = "WaitSpin" + _spinnerFrame.ToString("D2");
            GUIContent spinContent = EditorGUIUtility.IconContent(spinIcon);
            GUI.Label(new Rect(x, midY, 18, 18), spinContent, GUIStyle.none);
            x += 22;
        }

        // Phase text
        string phaseText = _statusMessage ?? "Idle";
        GUIStyle phaseStyle = new GUIStyle(EditorStyles.label)
        {
            fontStyle = FontStyle.Bold,
            fontSize = 11
        };
        Vector2 phaseSize = phaseStyle.CalcSize(new GUIContent(phaseText));
        GUI.Label(new Rect(x, midY, phaseSize.x + 4, 18), phaseText, phaseStyle);
        x += phaseSize.x + 12;

        // Separator
        EditorGUI.DrawRect(new Rect(x, barRect.y + 4, 1, barRect.height - 8), ThemeColors.SectionSeparator);
        x += 8;

        // Job ID
        string jobId = _jobService != null && _jobService.HasJob ? _jobService.CurrentJobId : "-";
        string shortId = jobId.Length > 8 ? jobId.Substring(0, 8) : jobId;
        GUIStyle idStyle = new GUIStyle(EditorStyles.miniLabel);
        GUIContent idContent = new GUIContent("Job: " + shortId, jobId); // full ID as tooltip
        Vector2 idSize = idStyle.CalcSize(idContent);
        GUI.Label(new Rect(x, midY + 2, idSize.x + 4, 16), idContent, idStyle);

        // Buttons on the right side. The "Start" button alone dispatches to the
        // single-run or batch path based on the _batchModeEnabled checkbox in
        // the Batch Evaluation foldout — there is no second activation toggle.
        float buttonWidth = 60;
        float buttonSpacing = 4;
        float startButtonWidth = _batchModeEnabled ? 110 : buttonWidth;
        float totalButtonsWidth = startButtonWidth + buttonSpacing
                                  + buttonWidth + buttonSpacing
                                  + buttonWidth + 8;
        float buttonX = barRect.xMax - totalButtonsWidth;
        float buttonY = barRect.y + (barRect.height - 20) * 0.5f;

        bool canStart = !_isStartingJob &&
                        !_isCancellingJob &&
                        !_isFetchingResult &&
                        !_jobService.IsJobActive &&
                        !_jobService.IsCancelPending &&
                        !_isBatchRunning &&
                        IsStartRequestValid();

        bool canCancel = !_isCancellingJob &&
                         ((_jobService.HasJob && !_jobService.IsInTerminalState) || _isBatchRunning);

        EditorGUI.BeginDisabledGroup(!canStart);
        string startLabel = _isStartingJob
            ? "Starting..."
            : (_batchModeEnabled ? $"Start Batch ({_batchRunCount})" : "Start");
        if (GUI.Button(new Rect(buttonX, buttonY, startButtonWidth, 20), startLabel))
        {
            if (_batchModeEnabled)
            {
                StartBatchAsync();
            }
            else
            {
                StartJobAsync();
            }
        }
        EditorGUI.EndDisabledGroup();
        buttonX += startButtonWidth + buttonSpacing;

        EditorGUI.BeginDisabledGroup(!canCancel);
        if (GUI.Button(new Rect(buttonX, buttonY, buttonWidth, 20), _isCancellingJob ? "Cancelling..." : "Cancel"))
        {
            CancelJobAsync();
        }
        EditorGUI.EndDisabledGroup();
        buttonX += buttonWidth + buttonSpacing;

        if (GUI.Button(new Rect(buttonX, buttonY, buttonWidth, 20), "Reset"))
        {
            ResetState();
        }

        // Bottom separator
        EditorGUI.DrawRect(new Rect(barRect.x, barRect.yMax - 1, barRect.width, 1), ThemeColors.SectionSeparator);
    }

    private void DrawErrorBar()
    {
        if (!string.IsNullOrWhiteSpace(_lastError))
        {
            EditorGUILayout.HelpBox(_lastError, MessageType.Error);
        }
    }

    private void UpdateSpinnerFrame()
    {
        double now = EditorApplication.timeSinceStartup;
        if (now >= _nextSpinnerUpdateAt)
        {
            _spinnerFrame = (_spinnerFrame + 1) % 12;
            _nextSpinnerUpdateAt = now + SpinnerIntervalSeconds;
            Repaint();
        }
    }
}
#endif
