using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Vivian.Backend.Dtos;

namespace Vivian.Backend.Client
{
    public sealed class VivianJobService
    {
        private const int DefaultMaxLogChars = 120000;

        private readonly VivianApiClient _apiClient;
        private readonly int _maxLogChars;
        private readonly StringBuilder _logBuffer = new StringBuilder();

        private int _nextLogOffset;
        private bool _resultFetched;
        private string _lastResultJson = string.Empty;

        public VivianJobService(VivianApiClient apiClient, int maxLogChars = DefaultMaxLogChars)
        {
            _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
            _maxLogChars = Math.Max(2000, maxLogChars);
        }

        public string CurrentJobId { get; private set; } = string.Empty;
        public JobStatusResponse LastKnownStatus { get; private set; }
        public JobResultResponse LastResult { get; private set; }
        public SceneReviewResponse LastSceneReview { get; private set; }
        public bool IsCancelPending { get; private set; }
        public int NextLogOffset => _nextLogOffset;
        public bool HasJob => !string.IsNullOrEmpty(CurrentJobId);
        public bool IsInTerminalState => LastKnownStatus != null && IsTerminalStatus(LastKnownStatus.Status);
        public bool IsJobActive => HasJob && !IsInTerminalState;
        public bool CanFetchResult => LastKnownStatus != null && LastKnownStatus.Status == JobStatus.SUCCEEDED;
        public string Logs => _logBuffer.ToString();
        public string LastResultJson => _lastResultJson;

        public event Action<JobStatusResponse> StatusChanged;
        public event Action<string> LogsAppended;
        public event Action<JobResultResponse> Completed;
        public event Action<JobStatusResponse> Failed;
        public event Action LogsCleared;

        public async Task<StartJobResponse> StartJobAsync(StartJobRequest requestDto, CancellationToken cancellationToken = default)
        {
            if (requestDto == null)
            {
                throw new ArgumentNullException(nameof(requestDto));
            }

            if (IsJobActive || IsCancelPending)
            {
                throw new InvalidOperationException("Cannot start a new job while another job is active.");
            }

            StartJobResponse response = await _apiClient.StartJobAsync(requestDto, cancellationToken);

            CurrentJobId = response.JobId;
            _nextLogOffset = 0;
            _resultFetched = false;
            _lastResultJson = string.Empty;
            LastResult = null;
            LastSceneReview = null;
            IsCancelPending = false;
            _logBuffer.Clear();
            LogsCleared?.Invoke();

            var status = new JobStatusResponse
            {
                JobId = response.JobId,
                Status = response.Status,
                Phase = JobPhase.QUEUED,
                Error = null
            };

            SetLastStatus(status, true);
            return response;
        }

        public async Task<JobStatusResponse> PollStatusAsync(CancellationToken cancellationToken = default)
        {
            EnsureHasJob();
            JobStatusResponse status = await _apiClient.GetJobStatusAsync(CurrentJobId, cancellationToken);
            SetLastStatus(status, true);
            return status;
        }

        public async Task<JobLogsResponse> PollLogsAsync(CancellationToken cancellationToken = default)
        {
            EnsureHasJob();
            JobLogsResponse logsResponse = await _apiClient.GetJobLogsAsync(CurrentJobId, _nextLogOffset, cancellationToken);

            if (logsResponse.NextOffset > _nextLogOffset)
            {
                _nextLogOffset = logsResponse.NextOffset;
            }

            if (!string.IsNullOrEmpty(logsResponse.Chunk))
            {
                _logBuffer.Append(logsResponse.Chunk);
                TrimLogsIfNeeded();
                LogsAppended?.Invoke(logsResponse.Chunk);
            }

            if (LastKnownStatus == null || LastKnownStatus.Status != logsResponse.Status)
            {
                var statusFromLogs = new JobStatusResponse
                {
                    JobId = logsResponse.JobId,
                    Status = logsResponse.Status,
                    Phase = LastKnownStatus != null ? LastKnownStatus.Phase : JobPhase.QUEUED,
                    Error = LastKnownStatus != null ? LastKnownStatus.Error : null
                };
                SetLastStatus(statusFromLogs, true);
            }

            return logsResponse;
        }

        public async Task<SceneReviewResponse> PollSceneReviewAsync(CancellationToken cancellationToken = default)
        {
            EnsureHasJob();
            SceneReviewResponse review = await _apiClient.GetSceneReviewAsync(CurrentJobId, cancellationToken);
            LastSceneReview = review;

            var statusFromReview = new JobStatusResponse
            {
                JobId = review.JobId,
                Status = review.Status,
                Phase = review.Phase,
                Error = review.Error
            };
            SetLastStatus(statusFromReview, true);
            return review;
        }

        public async Task<SceneReviewDecisionResponse> SubmitSceneReviewDecisionAsync(
            int revision,
            bool confirmed,
            string feedback,
            CancellationToken cancellationToken = default)
        {
            EnsureHasJob();
            if (revision <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(revision), "Revision must be greater than zero.");
            }

            string normalizedFeedback = string.IsNullOrWhiteSpace(feedback) ? string.Empty : feedback.Trim();
            if (!confirmed && normalizedFeedback.Length == 0)
            {
                throw new ArgumentException("Feedback is required when confirmation is false.", nameof(feedback));
            }

            var request = new SceneReviewDecisionRequest
            {
                Revision = revision,
                Confirmed = confirmed,
                Feedback = confirmed ? null : normalizedFeedback
            };

            SceneReviewDecisionResponse response = await _apiClient.SubmitSceneReviewDecisionAsync(CurrentJobId, request, cancellationToken);
            var statusFromDecision = new JobStatusResponse
            {
                JobId = response.JobId,
                Status = response.Status,
                Phase = response.Phase,
                Error = LastKnownStatus != null ? LastKnownStatus.Error : null
            };
            SetLastStatus(statusFromDecision, true);

            return response;
        }

        public async Task<JobResultResponse> FetchResultAsync(CancellationToken cancellationToken = default)
        {
            EnsureHasJob();
            if (!CanFetchResult)
            {
                throw new InvalidOperationException("Result can only be fetched when the job status is SUCCEEDED.");
            }

            if (_resultFetched && LastResult != null)
            {
                return LastResult;
            }

            JobResultResponse result = await _apiClient.GetJobResultAsync(CurrentJobId, cancellationToken);
            LastResult = result;
            _lastResultJson = JsonConvert.SerializeObject(result, Formatting.Indented);
            _resultFetched = true;
            Completed?.Invoke(result);
            return result;
        }

        public async Task<string> FetchResultJsonAsync(CancellationToken cancellationToken = default)
        {
            EnsureHasJob();
            if (!CanFetchResult)
            {
                throw new InvalidOperationException("Result can only be fetched when the job status is SUCCEEDED.");
            }

            if (_resultFetched && !string.IsNullOrWhiteSpace(_lastResultJson))
            {
                return _lastResultJson;
            }

            string raw = await _apiClient.GetJobResultRawJsonAsync(CurrentJobId, cancellationToken);
            _lastResultJson = TryFormatJson(raw);
            _resultFetched = true;
            return _lastResultJson;
        }

        public async Task<CancelJobResponse> CancelJobAsync(CancellationToken cancellationToken = default)
        {
            EnsureHasJob();
            if (IsInTerminalState)
            {
                throw new InvalidOperationException("Cannot cancel because the job is already in a terminal state.");
            }

            IsCancelPending = true;
            CancelJobResponse response = await _apiClient.CancelJobAsync(CurrentJobId, cancellationToken);

            var status = new JobStatusResponse
            {
                JobId = response.JobId,
                Status = response.Status,
                Phase = response.Status == JobStatus.CANCELLED
                    ? JobPhase.CANCELLED
                    : (LastKnownStatus != null ? LastKnownStatus.Phase : JobPhase.QUEUED),
                Error = LastKnownStatus != null ? LastKnownStatus.Error : null
            };
            SetLastStatus(status, true);

            if (IsTerminalStatus(response.Status))
            {
                IsCancelPending = false;
            }

            return response;
        }

        public void Reset()
        {
            CurrentJobId = string.Empty;
            LastKnownStatus = null;
            LastResult = null;
            LastSceneReview = null;
            IsCancelPending = false;
            _nextLogOffset = 0;
            _resultFetched = false;
            _lastResultJson = string.Empty;
            _logBuffer.Clear();
            LogsCleared?.Invoke();
        }

        public void ClearLogs()
        {
            _logBuffer.Clear();
            LogsCleared?.Invoke();
        }

        public static bool IsTerminalStatus(JobStatus status)
        {
            return status == JobStatus.SUCCEEDED ||
                   status == JobStatus.FAILED ||
                   status == JobStatus.CANCELLED;
        }

        private void EnsureHasJob()
        {
            if (!HasJob)
            {
                throw new InvalidOperationException("No active job. Start a job first.");
            }
        }

        private void SetLastStatus(JobStatusResponse nextStatus, bool emitEvents)
        {
            bool hasChanged = HasStatusChanged(nextStatus);
            LastKnownStatus = nextStatus;

            if (IsTerminalStatus(nextStatus.Status))
            {
                IsCancelPending = false;
            }

            if (!emitEvents || !hasChanged)
            {
                return;
            }

            StatusChanged?.Invoke(nextStatus);
            if (nextStatus.Status == JobStatus.FAILED)
            {
                Failed?.Invoke(nextStatus);
            }
        }

        private bool HasStatusChanged(JobStatusResponse nextStatus)
        {
            if (LastKnownStatus == null)
            {
                return true;
            }

            if (!string.Equals(LastKnownStatus.JobId, nextStatus.JobId, StringComparison.Ordinal))
            {
                return true;
            }

            if (LastKnownStatus.Status != nextStatus.Status)
            {
                return true;
            }

            if (LastKnownStatus.Phase != nextStatus.Phase)
            {
                return true;
            }

            return !string.Equals(LastKnownStatus.Error, nextStatus.Error, StringComparison.Ordinal);
        }

        private void TrimLogsIfNeeded()
        {
            if (_logBuffer.Length <= _maxLogChars)
            {
                return;
            }

            int removeCount = _logBuffer.Length - _maxLogChars;
            _logBuffer.Remove(0, removeCount);
        }

        private static string TryFormatJson(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return string.Empty;
            }

            try
            {
                object deserialized = JsonConvert.DeserializeObject(raw);
                return JsonConvert.SerializeObject(deserialized, Formatting.Indented);
            }
            catch
            {
                return raw;
            }
        }
    }
}
