using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Serilog;
using VeeamVhcAws.Core.Models;

namespace VeeamVhcAws.Core.State;

public class FindingState
{
    private static readonly ILogger Logger = Log.ForContext<FindingState>();
    private static readonly Regex CountSuffix = new(@"\s*\(\d+x in last \d+h\)", RegexOptions.Compiled);
    private readonly string _path;
    private Dictionary<string, object> _state;

    public FindingState(string stateFile = "./veeam-vhc-aws-state.json")
    {
        _path = stateFile;
        _state = Load();
    }

    private static string FindingKey(Finding finding, string server, string monitor)
    {
        // Normalize session-count suffix so the key is stable across count changes
        var msg = CountSuffix.Replace(finding.Message, "");
        var msgTrunc = msg.Length > 80 ? msg[..80] : msg;
        var identity = $"{server}|{monitor}|{finding.Resource}|{msgTrunc}";
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(identity));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private Dictionary<string, object> Load()
    {
        if (!File.Exists(_path))
            return new Dictionary<string, object>
            {
                ["findings"] = new Dictionary<string, object>(),
                ["last_run"] = null!,
            };

        try
        {
            var json = File.ReadAllText(_path);
            var doc = JsonDocument.Parse(json);
            return DeserializeState(doc.RootElement);
        }
        catch (Exception e)
        {
            Logger.Warning("Could not load state file {Path}: {Error}", _path, e.Message);
            return new Dictionary<string, object>
            {
                ["findings"] = new Dictionary<string, object>(),
                ["last_run"] = null!,
            };
        }
    }

    private static Dictionary<string, object> DeserializeState(JsonElement element)
    {
        var result = new Dictionary<string, object>();
        foreach (var prop in element.EnumerateObject())
        {
            result[prop.Name] = prop.Value.ValueKind switch
            {
                JsonValueKind.Object => DeserializeState(prop.Value),
                JsonValueKind.String => prop.Value.GetString()!,
                JsonValueKind.Number => prop.Value.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => prop.Value.ToString()
            };
        }
        return result;
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(_path));
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(_state, options);
            File.WriteAllText(_path, json);
        }
        catch (Exception e)
        {
            Logger.Error("Could not save state file {Path}: {Error}", _path, e.Message);
        }
    }

    private Dictionary<string, object> GetFindings()
    {
        if (_state.TryGetValue("findings", out var f) && f is Dictionary<string, object> findings)
            return findings;
        var empty = new Dictionary<string, object>();
        _state["findings"] = empty;
        return empty;
    }

    public (List<MonitorResult> FilteredResults, List<Finding> Resolved) ProcessResults(List<MonitorResult> results)
    {
        var now = DateTime.UtcNow.ToString("O");
        var currentKeys = new HashSet<string>();
        var newFindingsByResult = new Dictionary<int, List<Finding>>();
        var resolved = new List<Finding>();
        var findings = GetFindings();

        for (int i = 0; i < results.Count; i++)
        {
            var result = results[i];
            newFindingsByResult[i] = new List<Finding>();

            foreach (var finding in result.Findings)
            {
                // Pass through metric and OK findings
                if (finding.MetricName != null || finding.Severity == Severity.Ok)
                {
                    newFindingsByResult[i].Add(finding);
                    continue;
                }

                var key = FindingKey(finding, result.Server, result.Monitor.ToLowerString());
                currentKeys.Add(key);

                if (!findings.ContainsKey(key))
                {
                    // NEW finding
                    newFindingsByResult[i].Add(finding);
                    var entry = new Dictionary<string, object>
                    {
                        ["first_seen"] = now,
                        ["last_seen"] = now,
                        ["resource"] = finding.Resource,
                        ["message"] = finding.Message.Length > 100 ? finding.Message[..100] : finding.Message,
                        ["severity"] = finding.Severity.ToLowerString(),
                        ["server"] = result.Server,
                        ["monitor"] = result.Monitor.ToLowerString(),
                    };
                    if (finding.Details.TryGetValue("count", out var c))
                        entry["count"] = Convert.ToDouble(c);
                    findings[key] = entry;
                    Logger.Debug("New finding: {Resource}", finding.Resource);
                }
                else
                {
                    // EXISTING finding — update last_seen
                    if (findings[key] is Dictionary<string, object> prev)
                    {
                        prev["last_seen"] = now;
                        prev["severity"] = finding.Severity.ToLowerString();

                        // For session-count findings: suppress re-alert if count hasn't increased
                        if (finding.Details.TryGetValue("count", out var countObj))
                        {
                            var currentCount = Convert.ToDouble(countObj);
                            var prevCount = prev.TryGetValue("count", out var pc) && pc is double d ? d : 0.0;
                            prev["count"] = currentCount;
                            if (currentCount <= prevCount)
                            {
                                Logger.Debug("Suppressing session finding (count {Current} <= prev {Prev}): {Resource}",
                                    currentCount, prevCount, finding.Resource);
                                continue;
                            }
                        }
                    }
                    finding.Details = new Dictionary<string, object>(finding.Details)
                    {
                        ["_seen_before"] = true
                    };
                    newFindingsByResult[i].Add(finding);
                }
            }
        }

        // Find RESOLVED findings
        var staleKeys = new List<string>();
        foreach (var (key, info) in findings)
        {
            if (currentKeys.Contains(key))
                continue;

            if (info is Dictionary<string, object> infoDict)
            {
                resolved.Add(new Finding(
                    severity: Severity.Ok,
                    resource: infoDict.GetValueOrDefault("resource", "unknown")?.ToString() ?? "unknown",
                    message: $"RESOLVED: {infoDict.GetValueOrDefault("message", "unknown")}",
                    details: new Dictionary<string, object>
                    {
                        ["resolved_at"] = now,
                        ["first_seen"] = infoDict.GetValueOrDefault("first_seen", "")!,
                        ["was_severity"] = infoDict.GetValueOrDefault("severity", "")!,
                    }
                ));
                Logger.Information("Finding resolved: {Resource}",
                    infoDict.GetValueOrDefault("resource", "unknown"));
            }
            staleKeys.Add(key);
        }

        foreach (var key in staleKeys)
            findings.Remove(key);

        // Rebuild results with filtered findings
        var filteredResults = new List<MonitorResult>();
        for (int i = 0; i < results.Count; i++)
        {
            var r = results[i];
            filteredResults.Add(new MonitorResult(
                monitor: r.Monitor,
                timestamp: r.Timestamp,
                durationMs: r.DurationMs,
                overallSeverity: r.OverallSeverity,
                findings: newFindingsByResult[i],
                server: r.Server,
                errors: r.Errors,
                metadata: r.Metadata
            ));
        }

        _state["last_run"] = now;
        Save();

        return (filteredResults, resolved);
    }
}
