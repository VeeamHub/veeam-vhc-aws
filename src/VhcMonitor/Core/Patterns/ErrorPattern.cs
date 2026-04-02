using VhcMonitor.Core.Models;

namespace VhcMonitor.Core.Patterns;

public class ErrorPattern
{
    public string Pattern { get; set; } = "";
    public Severity Severity { get; set; }
    public string Message { get; set; } = "";
    public string Category { get; set; } = "";
    public List<string> ExtractFields { get; set; } = new();
    public string Source { get; set; } = "";
    public string LogHint { get; set; } = "";
    public string Remediation { get; set; } = "";

    public ErrorPattern() { }

    public ErrorPattern(string pattern, Severity severity, string message, string category,
        List<string>? extractFields = null, string source = "", string logHint = "", string remediation = "")
    {
        Pattern = pattern;
        Severity = severity;
        Message = message;
        Category = category;
        ExtractFields = extractFields ?? new List<string>();
        Source = source;
        LogHint = logHint;
        Remediation = remediation;
    }
}
