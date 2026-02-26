using System;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEngine;
using Vivian.Backend.Dtos;

namespace Vivian.Backend.Client
{
    public sealed class VivianApiException : Exception
    {
        public int? StatusCode { get; }
        public string Endpoint { get; }
        public string ResponseBodySnippet { get; }

        public VivianApiException(string message, int? statusCode, string endpoint, string responseBodySnippet, Exception innerException = null)
            : base(message, innerException)
        {
            StatusCode = statusCode;
            Endpoint = endpoint;
            ResponseBodySnippet = responseBodySnippet;
        }
    }

    public sealed class VivianApiClient : IDisposable
    {
        private static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        {
            MissingMemberHandling = MissingMemberHandling.Ignore
        };

        private readonly HttpClient _httpClient;
        private readonly bool _ownsHttpClient;
        private Uri _baseUri;

        public VivianApiClient(string serverUrl, TimeSpan? timeout = null, HttpClient httpClient = null)
        {
            _httpClient = httpClient ?? new HttpClient();
            _ownsHttpClient = httpClient == null;

            _httpClient.Timeout = timeout ?? TimeSpan.FromSeconds(45);
            EnsureJsonAcceptHeader();
            SetServerUrl(serverUrl);
        }

        public string ServerUrl => _baseUri.AbsoluteUri.TrimEnd('/');

        public void SetServerUrl(string serverUrl)
        {
            if (string.IsNullOrWhiteSpace(serverUrl))
            {
                throw new ArgumentException("Server URL cannot be empty.", nameof(serverUrl));
            }

            string trimmed = serverUrl.Trim();
            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out Uri parsedUri))
            {
                throw new ArgumentException("Invalid server URL: " + serverUrl, nameof(serverUrl));
            }

            string withTrailingSlash = parsedUri.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
                ? parsedUri.AbsoluteUri
                : parsedUri.AbsoluteUri + "/";

            _baseUri = new Uri(withTrailingSlash, UriKind.Absolute);
        }

        public Task<StartJobResponse> StartJobAsync(StartJobRequest request, CancellationToken cancellationToken = default)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            return SendJsonAsync<StartJobResponse>(HttpMethod.Post, "/jobs/start", request, cancellationToken);
        }

        public Task<HealthResponse> GetHealthAsync(CancellationToken cancellationToken = default)
        {
            return SendJsonAsync<HealthResponse>(HttpMethod.Get, "/health", null, cancellationToken);
        }

        public Task<JobStatusResponse> GetJobStatusAsync(string jobId, CancellationToken cancellationToken = default)
        {
            return SendJsonAsync<JobStatusResponse>(HttpMethod.Get, "/jobs/" + EscapePath(jobId) + "/status", null, cancellationToken);
        }

        public Task<JobLogsResponse> GetJobLogsAsync(string jobId, int? offset = null, CancellationToken cancellationToken = default)
        {
            string endpoint = "/jobs/" + EscapePath(jobId) + "/logs";
            if (offset.HasValue)
            {
                endpoint += "?offset=" + offset.Value.ToString(CultureInfo.InvariantCulture);
            }

            return SendJsonAsync<JobLogsResponse>(HttpMethod.Get, endpoint, null, cancellationToken);
        }

        public Task<JobResultResponse> GetJobResultAsync(string jobId, CancellationToken cancellationToken = default)
        {
            return SendJsonAsync<JobResultResponse>(HttpMethod.Get, "/jobs/" + EscapePath(jobId) + "/result", null, cancellationToken);
        }

        public Task<string> GetJobResultRawJsonAsync(string jobId, CancellationToken cancellationToken = default)
        {
            return SendRawAsync(HttpMethod.Get, "/jobs/" + EscapePath(jobId) + "/result", null, cancellationToken);
        }

        public async Task<CancelJobResponse> CancelJobAsync(string jobId, CancellationToken cancellationToken = default)
        {
            string endpoint = "/jobs/" + EscapePath(jobId) + "/cancel";

            try
            {
                return await SendJsonAsync<CancelJobResponse>(HttpMethod.Post, endpoint, new { }, cancellationToken);
            }
            catch (VivianApiException ex) when (ex.StatusCode == (int)HttpStatusCode.MethodNotAllowed)
            {
                return await SendJsonAsync<CancelJobResponse>(HttpMethod.Delete, endpoint, null, cancellationToken);
            }
        }

        private async Task<TResponse> SendJsonAsync<TResponse>(HttpMethod method, string endpoint, object requestBody, CancellationToken cancellationToken)
        {
            string responseBody = await SendRawAsync(method, endpoint, requestBody, cancellationToken);

            try
            {
                TResponse result = JsonConvert.DeserializeObject<TResponse>(responseBody, JsonSettings);
                if (result == null)
                {
                    throw new InvalidOperationException("Empty JSON payload.");
                }

                return result;
            }
            catch (Exception ex)
            {
                string snippet = CreateSnippet(responseBody, 300);
                throw new InvalidOperationException(
                    "Failed to parse JSON response for " + endpoint + ". Body snippet: " + snippet,
                    ex);
            }
        }

        private async Task<string> SendRawAsync(HttpMethod method, string endpoint, object requestBody, CancellationToken cancellationToken)
        {
            using (var request = CreateRequest(method, endpoint, requestBody))
            {
                Uri requestUri = request.RequestUri;
                try
                {
                    using (HttpResponseMessage response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
                    {
                        string responseBody = response.Content == null
                            ? string.Empty
                            : await response.Content.ReadAsStringAsync();

                        if (!response.IsSuccessStatusCode)
                        {
                            MaybeLogFailedRequest(method, requestUri, requestBody, (int)response.StatusCode, responseBody, null);
                            throw BuildApiException(method, endpoint, response, responseBody);
                        }

                        return responseBody;
                    }
                }
                catch (VivianApiException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    MaybeLogFailedRequest(method, requestUri, requestBody, null, null, ex);
                    throw;
                }
            }
        }

        private static void MaybeLogFailedRequest(HttpMethod method, Uri uri, object requestBody, int? statusCode, string responseBody, Exception exception)
        {
            if (uri == null)
            {
                return;
            }

            if (!string.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) || uri.Port != 8000)
            {
                return;
            }

            string message = "[VivianApiClient] request failed: " + method.Method + " " + uri.AbsoluteUri;
            if (statusCode.HasValue)
            {
                message += " | status: " + statusCode.Value.ToString(CultureInfo.InvariantCulture);
            }

            if (!string.IsNullOrWhiteSpace(responseBody))
            {
                message += " | response: " + CreateSnippet(responseBody, 1000);
            }

            if (requestBody != null)
            {
                string payload = JsonConvert.SerializeObject(requestBody, JsonSettings);
                message += " | body: " + CreateSnippet(payload, 1000);
            }

            if (exception != null)
            {
                message += " | exception: " + exception.GetType().Name + ": " + exception.Message;
            }

            Debug.LogWarning(message);
        }

        private HttpRequestMessage CreateRequest(HttpMethod method, string endpoint, object requestBody)
        {
            var request = new HttpRequestMessage(method, BuildUri(endpoint));
            request.Headers.Accept.Clear();
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            if (requestBody != null)
            {
                string json = JsonConvert.SerializeObject(requestBody, JsonSettings);
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            }

            return request;
        }

        private void EnsureJsonAcceptHeader()
        {
            bool hasJsonAccept = _httpClient.DefaultRequestHeaders.Accept.Any(
                h => string.Equals(h.MediaType, "application/json", StringComparison.OrdinalIgnoreCase));

            if (!hasJsonAccept)
            {
                _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            }
        }

        private Uri BuildUri(string endpoint)
        {
            string normalized = endpoint.StartsWith("/", StringComparison.Ordinal) ? endpoint.Substring(1) : endpoint;
            return new Uri(_baseUri, normalized);
        }

        private static string EscapePath(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("Value cannot be empty.", nameof(value));
            }

            return Uri.EscapeDataString(value.Trim());
        }

        private static VivianApiException BuildApiException(HttpMethod method, string endpoint, HttpResponseMessage response, string responseBody)
        {
            int statusCode = (int)response.StatusCode;
            string reason = string.IsNullOrWhiteSpace(response.ReasonPhrase) ? "No reason phrase." : response.ReasonPhrase;
            string bodySnippet = CreateSnippet(responseBody, 500);

            string message =
                "HTTP " + method.Method + " " + endpoint + " failed with status " +
                statusCode.ToString(CultureInfo.InvariantCulture) + " (" + reason + "). Body: " + bodySnippet;

            return new VivianApiException(message, statusCode, endpoint, bodySnippet);
        }

        private static string CreateSnippet(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "(empty)";
            }

            string compact = value
                .Replace("\r", " ")
                .Replace("\n", " ")
                .Trim();

            if (compact.Length <= maxLength)
            {
                return compact;
            }

            return compact.Substring(0, maxLength) + "...";
        }

        public void Dispose()
        {
            if (_ownsHttpClient)
            {
                _httpClient.Dispose();
            }
        }
    }
}
