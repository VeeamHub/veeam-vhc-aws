using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Serilog;
using VeeamVhcAws.Core.Auth;

namespace VeeamVhcAws.Core.Clients;

public class VbrClient : IVbrClient
{
    private static readonly ILogger Logger = Log.ForContext<VbrClient>();
    private readonly string _baseUrl;
    private readonly VbrAuth _auth;
    private readonly bool _verifySsl;
    private readonly int _timeout;
    private readonly int _retryCount;
    private readonly int _retryDelay;
    private readonly string _apiVersion;

    public VbrClient(string baseUrl, VbrAuth auth, bool verifySsl = true,
        int timeout = 30, int retryCount = 2, int retryDelay = 5,
        string apiVersion = "1.3-rev1")
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _auth = auth;
        _verifySsl = verifySsl;
        _timeout = timeout;
        _retryCount = retryCount;
        _retryDelay = retryDelay;
        _apiVersion = apiVersion;
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
        Dictionary<string, string>? queryParams = null, object? jsonBody = null)
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

                if (jsonBody != null)
                {
                    var bodyJson = JsonSerializer.Serialize(jsonBody,
                        new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                    request.Content = new StringContent(bodyJson, System.Text.Encoding.UTF8, "application/json");
                }

                var response = client.SendAsync(request).GetAwaiter().GetResult();
                var durationMs = sw.ElapsedMilliseconds;
                Logger.Debug("VBR {Method} {Path} -> {StatusCode} ({Duration}ms)",
                    method, path, (int)response.StatusCode, durationMs);

                response.EnsureSuccessStatusCode();

                if (response.StatusCode == HttpStatusCode.NoContent)
                    return new Dictionary<string, object>();

                var json = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                return DeserializeJson(json);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
            {
                var durationMs = sw.ElapsedMilliseconds;
                lastError = e;
                if (attempt < _retryCount)
                {
                    Logger.Warning("VBR {Method} {Path} failed (attempt {Attempt}/{Total}, {Duration}ms): {Error} — retrying in {Delay}s",
                        method, path, attempt + 1, _retryCount + 1, durationMs, e.Message, _retryDelay);
                    Thread.Sleep(_retryDelay * 1000);
                }
                else
                {
                    Logger.Error("VBR {Method} {Path} failed after {Total} attempts ({Duration}ms): {Error}",
                        method, path, _retryCount + 1, durationMs, e.Message);
                }
            }
        }

        throw lastError!;
    }

    private static Dictionary<string, object> DeserializeJson(string json)
    {
        var doc = JsonDocument.Parse(json);
        return JsonElementToDict(doc.RootElement);
    }

    internal static Dictionary<string, object> JsonElementToDict(JsonElement element)
    {
        var result = new Dictionary<string, object>();
        if (element.ValueKind != JsonValueKind.Object)
            return result;

        foreach (var prop in element.EnumerateObject())
        {
            result[prop.Name] = ConvertJsonElement(prop.Value);
        }
        return result;
    }

    internal static object ConvertJsonElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => JsonElementToDict(element),
            JsonValueKind.Array => element.EnumerateArray().Select(ConvertJsonElement).ToList(),
            JsonValueKind.String => element.GetString()!,
            JsonValueKind.Number when element.TryGetInt64(out var l) => l,
            JsonValueKind.Number => element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => element.ToString()
        };
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

    public List<Dictionary<string, object>> GetRepositoryStates()
    {
        var result = Request("GET", "/api/v1/backupInfrastructure/repositories/states");
        return ExtractDataList(result);
    }

    public List<Dictionary<string, object>> GetScaleoutRepositories()
    {
        var result = Request("GET", "/api/v1/backupInfrastructure/scaleOutRepositories");
        return ExtractDataList(result);
    }

    public List<Dictionary<string, object>> GetSessions(int lookbackHours = 24)
    {
        var cutoff = DateTime.UtcNow.AddHours(-lookbackHours);
        var createdAfter = cutoff.ToString("yyyy-MM-ddTHH:mm:ssZ");
        var allSessions = new List<Dictionary<string, object>>();
        int offset = 0;
        int pageSize = 50;

        while (true)
        {
            var result = Request("GET", "/api/v1/sessions",
                new Dictionary<string, string>
                {
                    ["createdAfter"] = createdAfter,
                    ["limit"] = pageSize.ToString(),
                    ["skip"] = offset.ToString()
                });
            var page = ExtractDataList(result);
            if (page.Count == 0)
                break;
            allSessions.AddRange(page);
            if (page.Count < pageSize)
                break;
            offset += pageSize;
        }

        Logger.Debug("Fetched {Count} sessions total (paginated)", allSessions.Count);
        return allSessions;
    }

    public List<Dictionary<string, object>> GetJobs()
    {
        var result = Request("GET", "/api/v1/jobs");
        return ExtractDataList(result);
    }

    public List<Dictionary<string, object>> GetBackups()
    {
        var allBackups = new List<Dictionary<string, object>>();
        int offset = 0;
        int pageSize = 50;
        int skipped = 0;

        while (true)
        {
            try
            {
                var result = Request("GET", "/api/v1/backups",
                    new Dictionary<string, string>
                    {
                        ["limit"] = pageSize.ToString(),
                        ["skip"] = offset.ToString()
                    });
                var page = ExtractDataList(result);
                if (page.Count == 0)
                    break;
                allBackups.AddRange(page);
                if (page.Count < pageSize)
                    break;
                offset += pageSize;
            }
            catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.InternalServerError)
            {
                Logger.Warning("VBR /api/v1/backups batch failed at offset {Offset} — falling back to per-item fetch", offset);
                bool endReached = false;
                for (int i = 0; i < pageSize; i++)
                {
                    try
                    {
                        var result = Request("GET", "/api/v1/backups",
                            new Dictionary<string, string>
                            {
                                ["limit"] = "1",
                                ["skip"] = (offset + i).ToString()
                            });
                        var page = ExtractDataList(result);
                        if (page.Count == 0)
                        {
                            endReached = true;
                            break;
                        }
                        allBackups.AddRange(page);
                    }
                    catch (HttpRequestException)
                    {
                        skipped++;
                        Logger.Warning("VBR /api/v1/backups entry at offset {Offset} cannot be serialized — skipped", offset + i);
                    }
                }
                if (endReached) break;
                offset += pageSize;
            }
        }

        if (skipped > 0)
            Logger.Information("Fetched {Count} backups, skipped {Skipped} unserializable entries", allBackups.Count, skipped);
        else
            Logger.Debug("Fetched {Count} backups total (paginated)", allBackups.Count);

        return allBackups;
    }

    public List<Dictionary<string, object>> GetRestorePoints(int limit = 500, int offset = 0)
    {
        var result = Request("GET", "/api/v1/restorePoints",
            new Dictionary<string, string>
            {
                ["limit"] = limit.ToString(),
                ["skip"] = offset.ToString()
            });
        return ExtractDataList(result);
    }

    public List<Dictionary<string, object>> RescanRepositories(List<string> repoIds)
    {
        var results = new List<Dictionary<string, object>>();
        foreach (var repoId in repoIds)
        {
            var result = Request("POST", $"/api/v1/backupInfrastructure/repositories/{repoId}/rescan");
            results.Add(result);
        }
        return results;
    }

    public Dictionary<string, object> GetServerInfo()
    {
        return Request("GET", "/api/v1/serverInfo");
    }

    public List<Dictionary<string, object>> GetManagedServers(string? typeFilter = null)
    {
        var queryParams = typeFilter != null
            ? new Dictionary<string, string> { ["typeFilter"] = typeFilter }
            : null;
        var result = Request("GET", "/api/v1/backupInfrastructure/managedServers", queryParams);
        return ExtractDataList(result);
    }

    public List<Dictionary<string, object>> GetInventory(string hostname,
        string hierarchyType = "VmsAndTemplates", int limit = 200)
    {
        var body = new
        {
            hierarchyType,
            pagination = new { skip = 0, limit }
        };
        var result = Request("POST", $"/api/v1/inventory/{Uri.EscapeDataString(hostname)}",
            jsonBody: body);
        return ExtractDataList(result);
    }

    public Dictionary<string, object> GetConnectionCertificate(string serverName,
        string credentialsId, string type = "ViHost", int port = 443)
    {
        var body = new
        {
            serverName,
            credentialsId,
            type,
            port
        };
        return Request("POST", "/api/v1/connectionCertificate", jsonBody: body);
    }
}
