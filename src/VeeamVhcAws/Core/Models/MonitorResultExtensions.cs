namespace VeeamVhcAws.Core.Models;

public static class MonitorResultExtensions
{
    /// <summary>
    /// True when these results represent a daily-summary dispatch (Metadata["summary"] == true).
    /// Single-sourced so the email and webhook handlers can't drift on how a summary is detected.
    /// </summary>
    public static bool IsSummary(this IReadOnlyList<MonitorResult> results) =>
        results.Any(r => r.Metadata.TryGetValue("summary", out var v) && v is true);
}
