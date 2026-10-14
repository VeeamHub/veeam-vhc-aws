using System.Text.Json;
using VeeamVhcAws.Core.Config;
using VeeamVhcAws.Core.Models;
using VeeamVhcAws.Core.State;

namespace VeeamVhcAws.Web.Services;

public record AlertView(
    string Key,
    string Severity,
    string Server,
    string Monitor,
    string Resource,
    string Message,
    DateTime? FirstSeen,
    DateTime? LastSeen,
    double Count,
    bool Suppressed);

public record StateSnapshot(
    DateTime? LastRun,
    IReadOnlyList<AlertView> ActiveFindings,
    IReadOnlyList<AlertView> CapturedErrors,
    IReadOnlyDictionary<string, DateTime> LastSuccessByKey);

public class StateService
{
    private readonly string _statePath;

    public StateService(WebHostOptions options)
    {
        _statePath = ConfigLoader.ResolveStateFile(options.ConfigPath);
    }

    public string StateFilePath => _statePath;

    public StateSnapshot Load()
    {
        var root = LoadRoot();
        var suppressions = AsDict(root.GetValueOrDefault("suppressions"));
        return new StateSnapshot(
            LastRun: TryParseDate(root.GetValueOrDefault("last_run")),
            ActiveFindings: ParseSection(root, "findings", suppressions),
            CapturedErrors: ParseSection(root, "captured_errors", suppressions),
            LastSuccessByKey: ParseLastSuccess(root));
    }

    public bool Suppress(string key)
    {
        lock (StateFile.Gate(_statePath))
        {
            using var hold = StateFile.Acquire(_statePath);
            return SuppressUnlocked(key);
        }
    }

    private bool SuppressUnlocked(string key)
    {
        var root = LoadRoot();
        var captured = AsDict(root.GetValueOrDefault("captured_errors"));
        var findings = AsDict(root.GetValueOrDefault("findings"));
        if (!captured.ContainsKey(key) && !findings.ContainsKey(key)) return false;

        var suppressions = AsDict(root.GetValueOrDefault("suppressions"));
        suppressions[key] = new Dictionary<string, object>
        {
            ["suppressed_at"] = DateTime.UtcNow.ToString("o"),
        };
        root["suppressions"] = suppressions;
        if (MarkCaptured(captured, key, suppressed: true))
            root["captured_errors"] = captured;
        Save(root);
        return true;
    }

    public bool Unsuppress(string key)
    {
        lock (StateFile.Gate(_statePath))
        {
            using var hold = StateFile.Acquire(_statePath);
            return UnsuppressUnlocked(key);
        }
    }

    private bool UnsuppressUnlocked(string key)
    {
        var root = LoadRoot();
        var suppressions = AsDict(root.GetValueOrDefault("suppressions"));
        var removed = suppressions.Remove(key);
        var captured = AsDict(root.GetValueOrDefault("captured_errors"));
        var cleared = MarkCaptured(captured, key, suppressed: false);
        if (!removed && !cleared) return false;

        root["suppressions"] = suppressions;
        if (cleared)
            root["captured_errors"] = captured;
        Save(root);
        return true;
    }

    private static bool MarkCaptured(Dictionary<string, object> captured, string key, bool suppressed)
    {
        if (captured.GetValueOrDefault(key) is not Dictionary<string, object> entry)
            return false;
        if (entry.GetValueOrDefault("suppressed") is bool current && current == suppressed)
            return false;
        entry["suppressed"] = suppressed;
        return true;
    }

    private Dictionary<string, object> LoadRoot()
    {
        if (!File.Exists(_statePath))
            return new Dictionary<string, object>
            {
                ["findings"] = new Dictionary<string, object>(),
                ["captured_errors"] = new Dictionary<string, object>(),
                ["suppressions"] = new Dictionary<string, object>(),
            };
        var json = File.ReadAllText(_statePath);
        var doc = JsonDocument.Parse(json);
        return Deserialize(doc.RootElement);
    }

    private void Save(Dictionary<string, object> root)
    {
        var json = JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true });
        StateFile.WriteAtomic(_statePath, json);
    }

    private static Dictionary<string, object> Deserialize(JsonElement el)
    {
        var result = new Dictionary<string, object>();
        if (el.ValueKind != JsonValueKind.Object) return result;
        foreach (var prop in el.EnumerateObject())
        {
            result[prop.Name] = prop.Value.ValueKind switch
            {
                JsonValueKind.Object => Deserialize(prop.Value),
                JsonValueKind.String => prop.Value.GetString()!,
                JsonValueKind.Number => prop.Value.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null!,
                _ => prop.Value.ToString(),
            };
        }
        return result;
    }

    private static Dictionary<string, object> AsDict(object? value) =>
        value as Dictionary<string, object> ?? new Dictionary<string, object>();

    private static List<AlertView> ParseSection(Dictionary<string, object> root, string section, Dictionary<string, object> suppressions)
    {
        var dict = AsDict(root.GetValueOrDefault(section));
        var results = new List<AlertView>(dict.Count);

        foreach (var (key, value) in dict)
        {
            var entry = AsDict(value);
            var sev = (entry.GetValueOrDefault("severity") as string ?? "warning").ToLowerInvariant();
            var server = entry.GetValueOrDefault("server") as string ?? "";
            var monitor = entry.GetValueOrDefault("monitor") as string ?? "";
            var resource = entry.GetValueOrDefault("resource") as string ?? "";
            var message = (entry.GetValueOrDefault("message") ?? entry.GetValueOrDefault("text")) as string ?? "";
            var firstSeen = TryParseDate(entry.GetValueOrDefault("first_seen"));
            var lastSeen = TryParseDate(entry.GetValueOrDefault("last_seen"));
            var count = entry.GetValueOrDefault("count") switch
            {
                double d => d,
                int i => (double)i,
                _ => 1.0,
            };
            var suppressedFlag = entry.GetValueOrDefault("suppressed") is true;
            var suppressed = suppressedFlag || suppressions.ContainsKey(key);

            results.Add(new AlertView(key, sev, server, monitor, resource, message, firstSeen, lastSeen, count, suppressed));
        }

        return results
            .OrderByDescending(a => SeverityRank(a.Severity))
            .ThenBy(a => a.Server)
            .ThenBy(a => a.Resource)
            .ToList();
    }

    private static Dictionary<string, DateTime> ParseLastSuccess(Dictionary<string, object> root)
    {
        var result = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        var dict = AsDict(root.GetValueOrDefault("lastSuccess"));
        foreach (var (k, v) in dict)
        {
            var ts = TryParseDate(v);
            if (ts.HasValue) result[k] = ts.Value;
        }
        return result;
    }

    private static DateTime? TryParseDate(object? value) =>
        value is string s && !string.IsNullOrWhiteSpace(s) && DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt)
            ? dt
            : null;

    private static int SeverityRank(string sev) => SeverityExtensions.ParseSeverity(sev).Rank();
}
