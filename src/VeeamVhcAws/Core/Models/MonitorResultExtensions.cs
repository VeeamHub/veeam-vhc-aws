namespace VeeamVhcAws.Core.Models;

public static class MonitorResultExtensions
{
    /// <summary>
    /// True when these results represent a daily-summary dispatch (Metadata["summary"] == true).
    /// Single-sourced so the email and webhook handlers can't drift on how a summary is detected.
    /// </summary>
    public static bool IsSummary(this IReadOnlyList<MonitorResult> results) =>
        results.Any(r => r.Metadata.TryGetValue("summary", out var v) && v is true);

    /// <summary>
    /// True when an alert run has something a deduplicating output should send: a non-OK finding
    /// that was not seen on the previous run, or a RESOLVED finding. Shared by every alert output
    /// so that "a problem that persists is silent" holds for all of them, not only ntfy.
    /// </summary>
    public static bool HasNewOrResolvedFindings(this IReadOnlyList<MonitorResult> results)
    {
        var findings = results.SelectMany(r => r.Findings).ToList();
        var hasNew = findings.Any(f =>
            f.Severity != Severity.Ok
            && f.MetricName == null
            && !(f.Details.TryGetValue("_seen_before", out var seen) && seen is true)
            && !f.Message.StartsWith("RESOLVED:"));
        return hasNew || findings.Any(f => f.Message.StartsWith("RESOLVED:"));
    }
}
