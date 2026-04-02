using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Serilog;
using VhcMonitor.Core.Auth;

namespace VhcMonitor.Core.Clients;

public class VbawsClient : IVbawsClient
{
    private static readonly ILogger Logger = Log.ForContext<VbawsClient>();
    private readonly string _baseUrl;
    private readonly VbawsAuth _auth;
    private readonly bool _verifySsl;
    private readonly int _timeout;
    private readonly int _retryCount;
    private readonly int _retryDelay;

    public VbawsClient(string baseUrl, VbawsAuth auth, bool verifySsl = true,
        int timeout = 30, int retryCount = 2, int retryDelay = 5)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _auth = auth;
        _verifySsl = verifySsl;
        _timeout = timeout;
        _retryCount = retryCount;
        _retryDelay = retryDelay;
    }

    private HttpClient CreateClient()
    {
        var handler = new HttpClientHandler();
        if (!_verifySsl)
            handler.ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;

        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(_timeout) };
    }

    private Dictionary<string, object> Request(string method, string path,
        Dictionary<string, string>? queryParams = null)
    {
        var url = $"{_baseUrl}{path}";
        if (queryParams != null && queryParams.Count > 0)
        {
            var qs = string.Join("&", queryParams.Select(kv =>
                $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
            url = $"{url}?{qs}";
        }

        Exception? lastError = null;
        for (int attempt = 0; attempt <= _retryCount; attempt++)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var headers = _auth.GetHeaders();
                using var client = CreateClient();
                var request = new HttpRequestMessage(new HttpMethod(method), url);
                foreach (var (key, value) in headers)
                    request.Headers.TryAddWithoutValidation(key, value);

                var response = client.SendAsync(request).GetAwaiter().GetResult();
                var durationMs = sw.ElapsedMilliseconds;
                Logger.Debug("VBAWS {Method} {Path} -> {StatusCode} ({Duration}ms)",
                    method, path, (int)response.StatusCode, durationMs);

                response.EnsureSuccessStatusCode();

                if (response.StatusCode == HttpStatusCode.NoContent)
                    return new Dictionary<string, object>();

                var json = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                return VbrClient.JsonElementToDict(JsonDocument.Parse(json).RootElement);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
            {
                var durationMs = sw.ElapsedMilliseconds;
                lastError = e;
                if (attempt < _retryCount)
                {
                    Logger.Warning("VBAWS {Method} {Path} failed (attempt {Attempt}/{Total}, {Duration}ms): {Error} — retrying in {Delay}s",
                        method, path, attempt + 1, _retryCount + 1, durationMs, e.Message, _retryDelay);
                    Thread.Sleep(_retryDelay * 1000);
                }
                else
                {
                    Logger.Error("VBAWS {Method} {Path} failed after {Total} attempts ({Duration}ms): {Error}",
                        method, path, _retryCount + 1, durationMs, e.Message);
                }
            }
        }

        throw lastError!;
    }

    private static List<Dictionary<string, object>> ExtractDataList(Dictionary<string, object> result)
    {
        object? data = null;
        if (result.TryGetValue("data", out data) || result.TryGetValue("Data", out data))
        {
            if (data is List<object> list)
                return list.OfType<Dictionary<string, object>>().ToList();
        }
        return new List<Dictionary<string, object>>();
    }

    public List<Dictionary<string, object>> GetSessions(DateTime from, DateTime to)
    {
        var queryParams = new Dictionary<string, string>
        {
            ["from"] = from.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ["to"] = to.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ["orderColumn"] = "CreationTime",
            ["orderAsc"] = "false",
        };
        var result = Request("GET", "/api/v1/sessions", queryParams);
        return ExtractDataList(result);
    }

    public Dictionary<string, object> GetSessionDetails(string sessionId)
    {
        return Request("GET", $"/api/v1/sessions/{sessionId}");
    }

    public List<Dictionary<string, object>> GetSessionLogs(string sessionId)
    {
        var result = Request("GET", $"/api/v1/sessions/{sessionId}/logs");
        return ExtractDataList(result);
    }

    public List<Dictionary<string, object>> GetHealthCheckSessions()
    {
        var result = Request("GET", "/api/v1/sessions",
            new Dictionary<string, string> { ["type"] = "HealthCheck" });
        return ExtractDataList(result);
    }
}
