using System;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;
using Vivian.Networking.Contracts;

namespace Vivian.Networking.Http
{
    public sealed class VivianApiClient
    {
        private readonly string _baseUrl;
        private readonly string _originUrl;
        private readonly int _timeoutSeconds;

        public VivianApiClient(string baseUrl, int timeoutSeconds = 120)
        {
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                throw new ArgumentException("Base URL must not be empty.", nameof(baseUrl));
            }

            _baseUrl = baseUrl.TrimEnd('/');
            _originUrl = GetOriginUrl(_baseUrl);
            _timeoutSeconds = Math.Max(1, timeoutSeconds);
        }

        public Task<HealthResponse> GetHealth()
        {
            return SendJsonAsync<HealthResponse>(UnityWebRequest.kHttpVerbGET, $"{_originUrl}/health");
        }

        public Task<InfoResponse> GetInfo()
        {
            return SendJsonAsync<InfoResponse>(UnityWebRequest.kHttpVerbGET, $"{_originUrl}/info");
        }

        public Task<JobResponse> CreateJob(JobCreateRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            return SendJsonAsync<JobResponse>(UnityWebRequest.kHttpVerbPOST, "jobs", request);
        }

        public Task<JobResponse> CreateJobWithFilePaths(
            string sceneJsonPath,
            string viewsManifestPath,
            string viewsDir,
            string outputDir)
        {
            var request = new JobCreateFilePathsRequest
            {
                scene_json_path = sceneJsonPath,
                views_manifest_path = viewsManifestPath,
                views_dir = viewsDir,
                output_dir = outputDir
            };

            return SendJsonAsync<JobResponse>(UnityWebRequest.kHttpVerbPOST, "jobs", request);
        }

        public Task<JobResponse> GetJob(string jobId)
        {
            ValidateJobId(jobId);
            return SendJsonAsync<JobResponse>(UnityWebRequest.kHttpVerbGET, $"jobs/{EscapePath(jobId)}");
        }

        public Task<JobResultResponse> GetJobResult(string jobId)
        {
            ValidateJobId(jobId);
            return SendJsonAsync<JobResultResponse>(UnityWebRequest.kHttpVerbGET, $"jobs/{EscapePath(jobId)}/result");
        }

        public Task<JobResponse> CancelJob(string jobId)
        {
            ValidateJobId(jobId);
            return SendJsonAsync<JobResponse>(UnityWebRequest.kHttpVerbPOST, $"jobs/{EscapePath(jobId)}/cancel");
        }

        private async Task<TResponse> SendJsonAsync<TResponse>(string method, string relativePath, object body = null)
        {
            using (var request = BuildRequest(method, relativePath, body))
            {
                await SendAsync(request);

                string responseText = request.downloadHandler != null ? request.downloadHandler.text : string.Empty;
                if (!IsSuccess(request))
                {
                    throw new VivianApiException(
                        $"HTTP {(long)request.responseCode} calling {request.url}",
                        request.responseCode,
                        responseText);
                }

                if (typeof(TResponse) == typeof(string))
                {
                    return (TResponse)(object)(responseText ?? string.Empty);
                }

                if (string.IsNullOrWhiteSpace(responseText))
                {
                    return default(TResponse);
                }

                try
                {
                    return JsonUtility.FromJson<TResponse>(responseText);
                }
                catch (Exception ex)
                {
                    throw new VivianApiException(
                        $"Failed to deserialize {typeof(TResponse).Name} from response at {request.url}: {ex.Message}",
                        request.responseCode,
                        responseText);
                }
            }
        }

        private UnityWebRequest BuildRequest(string method, string relativePath, object body)
        {
            string url = BuildUrl(relativePath);
            var request = new UnityWebRequest(url, method)
            {
                timeout = _timeoutSeconds,
                downloadHandler = new DownloadHandlerBuffer()
            };

            request.SetRequestHeader("Accept", "application/json");

            if (body != null)
            {
                string json = JsonUtility.ToJson(body);
                byte[] bodyRaw = Encoding.UTF8.GetBytes(json);
                request.uploadHandler = new UploadHandlerRaw(bodyRaw);
                request.SetRequestHeader("Content-Type", "application/json");
            }

            return request;
        }

        private static async Task SendAsync(UnityWebRequest request)
        {
            var op = request.SendWebRequest();
            while (!op.isDone)
            {
                await Task.Yield();
            }

            if (request.result == UnityWebRequest.Result.ConnectionError || request.result == UnityWebRequest.Result.DataProcessingError)
            {
                string responseText = request.downloadHandler != null ? request.downloadHandler.text : string.Empty;
                throw new VivianApiException(
                    $"Request failed: {request.error}",
                    request.responseCode,
                    responseText);
            }
        }

        private string BuildUrl(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                return _baseUrl;
            }

            if (Uri.TryCreate(relativePath, UriKind.Absolute, out _))
            {
                return relativePath;
            }

            return _baseUrl + "/" + relativePath.TrimStart('/');
        }

        private static string GetOriginUrl(string url)
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                return uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
            }

            return url.TrimEnd('/');
        }

        private static bool IsSuccess(UnityWebRequest request)
        {
            long code = request.responseCode;
            return code >= 200 && code <= 299;
        }

        private static string EscapePath(string value)
        {
            return Uri.EscapeDataString(value);
        }

        private static void ValidateJobId(string jobId)
        {
            if (string.IsNullOrWhiteSpace(jobId))
            {
                throw new ArgumentException("job_id must not be empty.", nameof(jobId));
            }
        }

        [Serializable]
        private class JobCreateFilePathsRequest
        {
            public string scene_json_path;
            public string views_manifest_path;
            public string views_dir;
            public string output_dir;
        }
    }

    public sealed class VivianApiException : Exception
    {
        public long StatusCode { get; }
        public string ResponseBody { get; }

        public VivianApiException(string message, long statusCode, string responseBody) : base(message)
        {
            StatusCode = statusCode;
            ResponseBody = responseBody;
        }
    }
}
