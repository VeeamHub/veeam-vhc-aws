namespace VhcMonitor.Core.Models;

public class Finding
{
    public Severity Severity { get; set; }
    public string Resource { get; set; } = "";
    public string Message { get; set; } = "";
    public Dictionary<string, object> Details { get; set; } = new();
    public string? MetricName { get; set; }
    public double? MetricValue { get; set; }

    public Finding() { }

    public Finding(Severity severity, string resource, string message,
        Dictionary<string, object>? details = null,
        string? metricName = null, double? metricValue = null)
    {
        Severity = severity;
        Resource = resource;
        Message = message;
        Details = details ?? new Dictionary<string, object>();
        MetricName = metricName;
        MetricValue = metricValue;
    }
}
