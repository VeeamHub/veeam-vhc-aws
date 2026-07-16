using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Serilog;
using VeeamVhcAws.Core.Auth;
using VeeamVhcAws.Core.Config;

namespace VeeamVhcAws.Core.Clients;

public class VbawsClient : IVbawsClient
{
    private static readonly ILogger Logger = Log.ForContext<VbawsClient>();
    private readonly string _baseUrl;
    private readonly VbawsAuth _auth;
    private readonly int _retryCount;
    private readonly int _retryDelay;
    // Single pooled client reused across all requests. A fresh HttpClient per request exhausts
    // sockets (TIME_WAIT) and re-handshakes TLS every call — pathological under the per-job
    // pagination fan-out (issue #16), which can make thousands of requests per run.
    private readonly HttpClient _http;

    public VbawsClient(string baseUrl, VbawsAuth auth, bool verifySsl = true,
        int timeout = 30, int retryCount = 2, int retryDelay = 5)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _auth = auth;
        _retryCount = retryCount;
        _retryDelay = retryDelay;

        var handler = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) };
        if (!verifySsl)
            handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(timeout) };
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
                var request = new HttpRequestMessage(new HttpMethod(method), url);
                foreach (var (key, value) in headers)
                    request.Headers.TryAddWithoutValidation(key, value);

                var response = _http.SendAsync(request).GetAwaiter().GetResult();
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

    private static Dictionary<string, string> BaseSessionQuery(DateTime from, DateTime to) => new()
    {
        ["from"] = from.ToString("yyyy-MM-ddTHH:mm:ssZ"),
        ["to"] = to.ToString("yyyy-MM-ddTHH:mm:ssZ"),
        ["orderColumn"] = "CreationTime",
        ["orderAsc"] = "false",
    };

    public List<Dictionary<string, object>> GetSessions(DateTime from, DateTime to)
    {
        var result = Request("GET", "/api/v1/sessions", BaseSessionQuery(from, to));
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

    // --- Issue #16: per-policy session scoping ---
    // NOTE (needs-live-validation): the policy-listing route and the per-job session filter
    // parameter below are ASSUMED against the VBAWS REST contract and must be confirmed against a
    // live appliance before this ships. They are isolated as constants so a correction is one edit.
    // The consumer (WorkerHealthMonitor) degrades gracefully to the global fetch if these return
    // nothing, so a wrong route cannot reduce existing coverage — only fail to add to it.
    private const string PoliciesPath = "/api/v1/policies";
    private const string JobFilterParam = "jobId";

    public List<Dictionary<string, object>> GetPolicies()
    {
        var result = Request("GET", PoliciesPath);
        return ExtractDataList(result);
    }

    public List<Dictionary<string, object>> GetSessionsForJob(string jobId, DateTime from, DateTime to)
    {
        const int pageSize = 200;
        const int maxPages = 50; // circuit breaker: cap 10k sessions per job
        var all = new List<Dictionary<string, object>>();
        var seenIds = new HashSet<string>();

        for (int page = 0; page < maxPages; page++)
        {
            var queryParams = BaseSessionQuery(from, to);
            queryParams[JobFilterParam] = jobId;
            queryParams["limit"] = pageSize.ToString();
            queryParams["skip"] = (page * pageSize).ToString();
            var batch = ExtractDataList(Request("GET", "/api/v1/sessions", queryParams));
            if (batch.Count == 0) break;

            // The VBAWS API is known to ignore some query params and filter client-side. If it
            // ignores skip/limit, every page returns the same rows — so stop as soon as a page
            // contributes no new session id, rather than re-fetching identical data up to maxPages.
            int added = 0;
            foreach (var s in batch)
            {
                var id = s.GetApiString("id", "Id", "");
                if (id.Length == 0 || seenIds.Add(id))
                {
                    all.Add(s);
                    added++;
                }
            }
            if (added == 0 || batch.Count < pageSize) break;
        }

        return all;
    }
}
