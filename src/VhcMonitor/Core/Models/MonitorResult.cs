using System.Text.Json;
using System.Text.Json.Serialization;

namespace VhcMonitor.Core.Models;

public class MonitorResult
{
    public MonitorType Monitor { get; set; }
    public DateTime Timestamp { get; set; }
    public int DurationMs { get; set; }
    public Severity OverallSeverity { get; set; }
    public List<Finding> Findings { get; set; } = new();
    public string Server { get; set; } = "";
    public List<string> Errors { get; set; } = new();
    public Dictionary<string, object> Metadata { get; set; } = new();

    public MonitorResult() { }

    public MonitorResult(MonitorType monitor, DateTime timestamp, int durationMs,
        Severity overallSeverity, List<Finding> findings,
        string server = "", List<string>? errors = null,
        Dictionary<string, object>? metadata = null)
    {
        Monitor = monitor;
        Timestamp = timestamp;
        DurationMs = durationMs;
        OverallSeverity = overallSeverity;
        Findings = findings;
        Server = server;
        Errors = errors ?? new List<string>();
        Metadata = metadata ?? new Dictionary<string, object>();
    }

    public Dictionary<string, object> ToDictionary()
    {
        var findings = new List<Dictionary<string, object>>();
        foreach (var f in Findings)
        {
            var fd = new Dictionary<string, object>
            {
                ["severity"] = f.Severity.ToLowerString(),
                ["resource"] = f.Resource,
                ["message"] = f.Message,
                ["details"] = f.Details,
            };
            if (f.MetricName != null)
                fd["metric_name"] = f.MetricName;
            if (f.MetricValue.HasValue)
                fd["metric_value"] = f.MetricValue.Value;
            findings.Add(fd);
        }

        var result = new Dictionary<string, object>
        {
            ["monitor"] = Monitor.ToLowerString(),
            ["timestamp"] = Timestamp.ToString("O"),
            ["duration_ms"] = DurationMs,
            ["overall_severity"] = OverallSeverity.ToLowerString(),
            ["findings"] = findings,
            ["errors"] = Errors,
            ["metadata"] = Metadata,
        };

        if (!string.IsNullOrEmpty(Server))
            result["server"] = Server;

        return result;
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public string ToJson()
    {
        return JsonSerializer.Serialize(ToDictionary(), SerializerOptions);
    }
}
