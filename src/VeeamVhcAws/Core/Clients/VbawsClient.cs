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
    // The global GET /sessions is capped (undocumented, empirically ~5k). GET /sessions supports a
    // server-side PolicyId filter (VBAWS REST 1.8-rev0), so querying per policy retrieves each
    // policy's own set independent of the global cap. Routes + params below are from the official
    // VBAWS 10 (1.8-rev0) reference. The consumer degrades to the global fetch if these return
    // nothing, so coverage can never shrink. Still needs-live-validation for ONE thing: whether the
    // cap resets per PolicyId (expected) — the contract does not document the cap at all.
    private const string SessionPolicyFilterParam = "PolicyId";

    // Policies are enumerated per workload — there is no single /policies collection.
    private static readonly string[] PolicyRoutes =
    {
        "/api/v1/virtualMachines/policies",     // EC2
        "/api/v1/policy/ec2SlaBasedPolicies",   // EC2 SLA-based
        "/api/v1/rds/policies",                 // RDS
        "/api/v1/efs/policies",                 // EFS
        "/api/v1/dynamoDb/policies",            // DynamoDB
        "/api/v1/fsx/policies",                 // FSx
        "/api/v1/redshift/policies",            // Redshift
        "/api/v1/redshiftServerless/policies",  // Redshift Serverless
    };

    public List<Dictionary<string, object>> GetPolicies()
    {
        var all = new List<Dictionary<string, object>>();
        foreach (var route in PolicyRoutes)
        {
            try
            {
                all.AddRange(ExtractDataList(Request("GET", route)));
            }
            catch (Exception e)
            {
                // A workload may be unlicensed/unconfigured (e.g. 404) — skip it, keep the rest.
                Logger.Debug("VBAWS policy route {Route} unavailable: {Error}", route, e.Message);
            }
        }
        return all;
    }

    public List<Dictionary<string, object>> GetSessionsForJob(string policyId, DateTime from, DateTime to)
    {
        const int pageSize = 200;
        const int maxPages = 50; // circuit breaker: cap 10k sessions per policy
        var all = new List<Dictionary<string, object>>();
        var seenIds = new HashSet<string>();

        for (int page = 0; page < maxPages; page++)
        {
            // Documented VBAWS 1.8-rev0 params: PolicyId (server-side scope), Limit, Offset, Sort.
            // Lookback windowing is applied client-side by the caller (FilterByLookback), so
            // FromUtc/ToUtc are intentionally omitted here.
            var queryParams = new Dictionary<string, string>
            {
                [SessionPolicyFilterParam] = policyId,
                ["Limit"] = pageSize.ToString(),
                ["Offset"] = (page * pageSize).ToString(),
                ["Sort"] = "startTimeDesc",
            };
            var batch = ExtractDataList(Request("GET", "/api/v1/sessions", queryParams));
            if (batch.Count == 0) break;

            // Defensive: if a server ignores Offset and returns the same rows each page, stop as
            // soon as a page contributes no new session id, rather than looping to maxPages.
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
