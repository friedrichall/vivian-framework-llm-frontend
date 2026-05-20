#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;
using Vivian.Backend.Dtos;

public sealed partial class VivianBackendWindow
{
    private bool _isBatchRunning;
    private bool _batchCancelRequested;

    private const string BatchOutputRootSegment = "Packages/vivian-example-prototypes/Resources/_batchmode";

    [Serializable]
    private sealed class BatchRunRecord
    {
        public int run_index;
        public string job_id;
        public string status;
        public string started_at;
        public string finished_at;
        public long duration_ms;
        public string metrics_file;
        public string run_output_dir;
        public string error;
    }

    [Serializable]
    private sealed class BatchSummary
    {
        public string batch_id;
        public string group_name;
        public string scene_input_dir;
        public int total_runs;
        public int succeeded;
        public int failed;
        public int cancelled;
        public string started_at;
        public string finished_at;
        public List<BatchRunRecord> runs = new List<BatchRunRecord>();
    }

    /// <summary>
    /// Executes _batchRunCount sequential pipeline runs with identical inputs.
    /// A new run only starts after the previous one reaches a terminal state.
    /// Outputs and metrics are aggregated under Resources/_batchmode/{batchId}/.
    /// </summary>
    private async void StartBatchAsync()
    {
        if (_isBatchRunning || _isStartingJob || _jobService == null)
        {
            return;
        }
        if (!TryApplyServerUrl(persist: false))
        {
            return;
        }

        int totalRuns = Mathf.Max(1, _batchRunCount);
        bool autoConfirm = _batchAutoConfirmScene;
        string safeGroupName = string.IsNullOrWhiteSpace(_groupName) ? "UnknownGroup" : _groupName.Trim();
        string batchId = string.IsNullOrWhiteSpace(_batchIdOverride)
            ? $"batch-{safeGroupName}-{DateTime.UtcNow:yyyyMMdd-HHmmss}"
            : _batchIdOverride.Trim();

        _isBatchRunning = true;
        _batchCancelRequested = false;
        _lastError = string.Empty;
        _statusMessage = $"Batch {batchId}: preparing 1/{totalRuns}...";
        Repaint();

        // One-time scene export — all runs reuse the same inputs.
        try
        {
            if (!CollectSelectedObjects())
            {
                throw new InvalidOperationException("Select at least one GameObject before starting.");
            }
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

            if (!string.IsNullOrWhiteSpace(_screensDir))
            {
                CopyScreensToGroupDir(_screensDir, _groupPath);
                AssetDatabase.Refresh();
            }
        }
        catch (Exception ex)
        {
            _lastError = "Batch setup failed: " + ex.Message;
            _statusMessage = "Idle";
            _isBatchRunning = false;
            Repaint();
            return;
        }

        string batchOutputRoot = Path.Combine(
            Path.GetFullPath(BatchOutputRootSegment),
            batchId);
        Directory.CreateDirectory(batchOutputRoot);

        // Snapshot the Unity-exported scene prefab (scene.json + views_manifest.json
        // + screens/ + any Model.prefab in groupPath) so every batch is fully
        // reproducible from its own folder alone. Excludes FunctionalSpecification/
        // — that's the agent OUTPUT, not the scene INPUT.
        string sceneInputDir = Path.Combine(batchOutputRoot, "scene_input");
        try
        {
            CopySceneInputSnapshot(_groupPath, sceneInputDir);
        }
        catch (Exception ex)
        {
            _lastError = "Scene snapshot failed: " + ex.Message;
            // Non-fatal — continue with the batch but record it as a warning.
        }

        BatchSummary summary = new BatchSummary
        {
            batch_id = batchId,
            group_name = safeGroupName,
            total_runs = totalRuns,
            started_at = DateTime.UtcNow.ToString("o"),
            scene_input_dir = "scene_input",
        };

        for (int runIndex = 1; runIndex <= totalRuns; runIndex++)
        {
            if (_batchCancelRequested)
            {
                summary.cancelled = totalRuns - (runIndex - 1);
                break;
            }

            BatchRunRecord record = new BatchRunRecord
            {
                run_index = runIndex,
                started_at = DateTime.UtcNow.ToString("o"),
            };
            _statusMessage = $"Batch {batchId}: run {runIndex}/{totalRuns} starting...";
            Repaint();

            try
            {
                StartJobRequest request = BuildStartRequest();
                request.AutoConfirmScene = autoConfirm;
                request.BatchId = batchId;
                request.BatchRunIndex = runIndex;
                request.BatchTotal = totalRuns;

                ResetSceneConfirmationForNewGeneration();
                _jobService.Reset();
                _logsText = string.Empty;
                _resultText = string.Empty;
                _resultFetchedForCurrentJob = false;

                StartJobResponse startResponse = await _jobService.StartJobAsync(request);
                record.job_id = startResponse.JobId;
                _statusMessage = $"Batch {batchId}: run {runIndex}/{totalRuns} job {startResponse.JobId.Substring(0, 8)}...";
                Repaint();

                await PollUntilTerminalAsync(autoConfirm, runIndex, totalRuns, batchId);

                JobStatusResponse last = _jobService.LastKnownStatus;
                record.status = last != null ? last.Status.ToString() : "UNKNOWN";
                if (last != null && !string.IsNullOrEmpty(last.Error))
                {
                    record.error = last.Error;
                }
            }
            catch (Exception ex)
            {
                record.status = "FAILED";
                record.error = ex.Message;
                _lastError = $"Batch run {runIndex} failed: {ex.Message}";
            }
            finally
            {
                record.finished_at = DateTime.UtcNow.ToString("o");
                if (DateTime.TryParse(record.started_at, out DateTime s) &&
                    DateTime.TryParse(record.finished_at, out DateTime f))
                {
                    record.duration_ms = (long)(f - s).TotalMilliseconds;
                }
            }

            string runFolder = $"run_{runIndex:D2}_{(record.status ?? "UNKNOWN")}";
            string runOutputDir = Path.Combine(batchOutputRoot, runFolder);
            CopyRunArtifacts(runOutputDir, record);
            record.run_output_dir = runFolder;

            if (record.status == "SUCCEEDED") summary.succeeded++;
            else if (record.status == "CANCELLED") summary.cancelled++;
            else summary.failed++;
            summary.runs.Add(record);

            WriteBatchSummary(batchOutputRoot, summary);
        }

        summary.finished_at = DateTime.UtcNow.ToString("o");
        WriteBatchSummary(batchOutputRoot, summary);

        _statusMessage = $"Batch {batchId} done: succeeded={summary.succeeded}, failed={summary.failed}, cancelled={summary.cancelled}.";
        _isBatchRunning = false;
        _batchCancelRequested = false;
        AssetDatabase.Refresh();
        Repaint();
    }

    /// <summary>
    /// Polls status/logs until the current job reaches a terminal state. When
    /// auto_confirm_scene is false, submits a confirm decision once the
    /// AWAITING_SCENE_CONFIRMATION phase is reached.
    /// </summary>
    private async Task PollUntilTerminalAsync(bool autoConfirm, int runIndex, int totalRuns, string batchId)
    {
        const int pollIntervalMs = 500;
        const int maxIdleSeconds = 60 * 60; // 1h safety net per run
        DateTime deadline = DateTime.UtcNow.AddSeconds(maxIdleSeconds);

        while (!_jobService.IsInTerminalState)
        {
            if (_batchCancelRequested)
            {
                try { await _jobService.CancelJobAsync(); } catch { /* best-effort */ }
                break;
            }
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Run exceeded 1h watchdog.");
            }

            await Task.Delay(pollIntervalMs);

            try { await _jobService.PollStatusAsync(); } catch { /* transient — retry next tick */ }
            try { await _jobService.PollLogsAsync(); } catch { /* transient — retry next tick */ }

            if (!autoConfirm && _jobService.LastKnownStatus != null &&
                _jobService.LastKnownStatus.Phase == JobPhase.AWAITING_SCENE_CONFIRMATION)
            {
                try
                {
                    SceneReviewResponse review = await _jobService.PollSceneReviewAsync();
                    if (review?.SceneReview != null && review.ReviewState == SceneReviewState.PENDING)
                    {
                        await _jobService.SubmitSceneReviewDecisionAsync(
                            review.SceneReview.Revision, true, null);
                    }
                }
                catch
                {
                    /* will retry next tick if still pending */
                }
            }

            _logsText = _jobService.Logs;
            _scrollLogsToEnd = true;
            _statusMessage = $"Batch {batchId}: run {runIndex}/{totalRuns} {_jobService.LastKnownStatus?.Phase}";
            Repaint();
        }
    }

    /// <summary>
    /// Copies the produced FunctionalSpecification/ directory and the orchestrator
    /// metrics.json into the per-run batch output folder so each run is preserved
    /// independently from the next.
    /// </summary>
    private void CopyRunArtifacts(string runOutputDir, BatchRunRecord record)
    {
        try
        {
            Directory.CreateDirectory(runOutputDir);

            string fsDir = Path.Combine(_groupPath, "FunctionalSpecification");
            if (Directory.Exists(fsDir))
            {
                string targetFs = Path.Combine(runOutputDir, "FunctionalSpecification");
                CopyDirectoryRecursive(fsDir, targetFs);
            }

            string metricsPath = FindLatestMetricsForJob(record.job_id);
            if (!string.IsNullOrEmpty(metricsPath) && File.Exists(metricsPath))
            {
                string targetMetrics = Path.Combine(runOutputDir, "metrics.json");
                File.Copy(metricsPath, targetMetrics, overwrite: true);
                record.metrics_file = Path.Combine(Path.GetFileName(runOutputDir), "metrics.json").Replace('\\', '/');
            }
        }
        catch (Exception ex)
        {
            // Don't fail the batch over an IO hiccup; surface it in the record.
            record.error = string.IsNullOrEmpty(record.error)
                ? $"copy_artifacts: {ex.Message}"
                : record.error + "; copy_artifacts: " + ex.Message;
        }
    }

    private static void CopyDirectoryRecursive(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (string filePath in Directory.GetFiles(sourceDir))
        {
            string fileName = Path.GetFileName(filePath);
            File.Copy(filePath, Path.Combine(destDir, fileName), overwrite: true);
        }
        foreach (string subDir in Directory.GetDirectories(sourceDir))
        {
            string subName = Path.GetFileName(subDir);
            CopyDirectoryRecursive(subDir, Path.Combine(destDir, subName));
        }
    }

    // Whitelist for scene_input/: scene.json + views_manifest.json plus the
    // 3D scene prefab (Model.prefab / .fbx / .blend / .obj / .gltf / .glb)
    // and the views/ and screens/ directories. Everything else
    // (FunctionalSpecification/, materials/, etc.) is intentionally excluded.
    private static readonly string[] SceneInputFileWhitelist =
    {
        "scene.json",
        "views_manifest.json",
    };

    private static readonly string[] SceneInputModelExtensions =
    {
        ".prefab", ".fbx", ".blend", ".obj", ".gltf", ".glb",
    };

    private static readonly string[] SceneInputDirWhitelist =
    {
        "views",
        "screens",
    };

    /// <summary>
    /// Copies a minimal, whitelisted scene-input snapshot from
    /// <paramref name="groupPath"/> to <paramref name="destDir"/>:
    /// scene.json, views_manifest.json, the 3D scene prefab (Model.prefab /
    /// .fbx / .blend / .obj / .gltf / .glb), and the views/ and screens/
    /// directories.
    /// </summary>
    private static void CopySceneInputSnapshot(string groupPath, string destDir)
    {
        if (string.IsNullOrEmpty(groupPath) || !Directory.Exists(groupPath))
        {
            return;
        }
        Directory.CreateDirectory(destDir);

        foreach (string filePath in Directory.GetFiles(groupPath))
        {
            string fileName = Path.GetFileName(filePath);
            string ext = Path.GetExtension(fileName);
            bool isWhitelisted = false;
            for (int i = 0; i < SceneInputFileWhitelist.Length; i++)
            {
                if (string.Equals(fileName, SceneInputFileWhitelist[i], StringComparison.OrdinalIgnoreCase))
                {
                    isWhitelisted = true;
                    break;
                }
            }
            if (!isWhitelisted)
            {
                for (int i = 0; i < SceneInputModelExtensions.Length; i++)
                {
                    if (string.Equals(ext, SceneInputModelExtensions[i], StringComparison.OrdinalIgnoreCase))
                    {
                        isWhitelisted = true;
                        break;
                    }
                }
            }
            if (!isWhitelisted)
            {
                continue;
            }
            File.Copy(filePath, Path.Combine(destDir, fileName), overwrite: true);
        }

        for (int i = 0; i < SceneInputDirWhitelist.Length; i++)
        {
            string dirName = SceneInputDirWhitelist[i];
            string source = Path.Combine(groupPath, dirName);
            if (Directory.Exists(source))
            {
                CopyDirectoryRecursiveSkipMeta(source, Path.Combine(destDir, dirName));
            }
        }
    }

    private static void CopyDirectoryRecursiveSkipMeta(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (string filePath in Directory.GetFiles(sourceDir))
        {
            if (filePath.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            string fileName = Path.GetFileName(filePath);
            File.Copy(filePath, Path.Combine(destDir, fileName), overwrite: true);
        }
        foreach (string subDir in Directory.GetDirectories(sourceDir))
        {
            string subName = Path.GetFileName(subDir);
            CopyDirectoryRecursiveSkipMeta(subDir, Path.Combine(destDir, subName));
        }
    }

    /// <summary>
    /// Locates the most recent metrics.json belonging to this job. The pipeline
    /// writes it into logs/orchestrator/batch-runs/{batch_id}/{timestamp}/.
    /// Falls back to scanning if the job_id is not encoded in the run folder name.
    /// </summary>
    private string FindLatestMetricsForJob(string jobId)
    {
        if (string.IsNullOrEmpty(jobId))
        {
            return null;
        }

        // The pipeline workspace_root is the directory FastAPI was started in.
        // We search a relative path from the Unity project root upwards — by
        // convention the backend repo sits alongside this Unity project.
        string[] candidateRoots = new[]
        {
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..")),
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "..")),
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "vivian-llm-specgen")),
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "..", "vivian-llm-specgen")),
        };

        foreach (string root in candidateRoots)
        {
            string batchRunsRoot = Path.Combine(root, "logs", "orchestrator", "batch-runs");
            if (!Directory.Exists(batchRunsRoot)) continue;
            foreach (string metricsPath in Directory.GetFiles(batchRunsRoot, "metrics.json", SearchOption.AllDirectories))
            {
                try
                {
                    string contents = File.ReadAllText(metricsPath);
                    if (contents.IndexOf(jobId, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return metricsPath;
                    }
                }
                catch { /* skip unreadable */ }
            }
        }
        return null;
    }

    private static void WriteBatchSummary(string batchOutputRoot, BatchSummary summary)
    {
        string path = Path.Combine(batchOutputRoot, "batch_summary.json");
        File.WriteAllText(path, JsonConvert.SerializeObject(summary, Formatting.Indented));
    }
}
#endif
