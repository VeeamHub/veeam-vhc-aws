using Serilog;
using VhcMonitor.Core.Models;

namespace VhcMonitor.Infrastructure;

public static class CrossCorrelator
{
    private static readonly ILogger Logger = Log.ForContext(typeof(CrossCorrelator));

    public static List<MonitorResult> Correlate(List<MonitorResult> results)
    {
        // Group results by server
        var byServer = new Dictionary<string, List<MonitorResult>>();
        foreach (var r in results)
            (byServer.TryGetValue(r.Server, out var list) ? list : (byServer[r.Server] = new List<MonitorResult>())).Add(r);

        foreach (var (serverName, serverResults) in byServer)
        {
            var allFindings = new List<(string MonitorValue, Finding Finding)>();
            foreach (var r in serverResults)
                foreach (var f in r.Findings)
                    allFindings.Add((r.Monitor.ToLowerString(), f));

            var correlations = new List<Finding>();

            var hasCredentialFailure = allFindings.Any(x =>
                x.Finding.Message.Contains("credential", StringComparison.OrdinalIgnoreCase) ||
                x.Finding.Message.Contains("access key", StringComparison.OrdinalIgnoreCase));
            var hasRetentionFailure = allFindings.Any(x =>
                x.MonitorValue == "worker_health" &&
                x.Finding.Severity is Severity.Critical or Severity.Error);

            if (hasCredentialFailure && hasRetentionFailure)
            {
                correlations.Add(new Finding(Severity.Critical,
                    $"[{serverName}] cross-correlation",
                    "Credential expiry cascade — credential failure is likely causing retention failures",
                    new Dictionary<string, object> { ["correlation"] = "M1+M3" }));
            }

            var hasRetentionViolation = allFindings.Any(x =>
                x.MonitorValue == "retention" &&
                x.Finding.Severity is Severity.Warning or Severity.Critical);
            var hasSubnetExhaustion = allFindings.Any(x =>
                x.Finding.Message.Contains("subnet", StringComparison.OrdinalIgnoreCase) &&
                x.Finding.Message.Contains("exhaustion", StringComparison.OrdinalIgnoreCase));

            if (hasRetentionViolation && hasSubnetExhaustion)
            {
                correlations.Add(new Finding(Severity.Critical,
                    $"[{serverName}] cross-correlation",
                    "Root cause: subnet IP exhaustion — workers cannot deploy, causing retention violations",
                    new Dictionary<string, object> { ["correlation"] = "M2+M3" }));
            }

            var hasOrphaned = allFindings.Any(x =>
                x.Finding.Message.Contains("orphan", StringComparison.OrdinalIgnoreCase));
            if (hasOrphaned)
            {
                correlations.Add(new Finding(Severity.Warning,
                    $"[{serverName}] cross-correlation",
                    "No active job for cleanup — orphaned backups detected without associated jobs",
                    new Dictionary<string, object> { ["correlation"] = "M2" }));
            }

            if (correlations.Count > 0)
            {
                var worstSev = correlations.Max(c => c.Severity.Rank());
                var overall = correlations.First(c => c.Severity.Rank() == worstSev).Severity;

                results.Add(new MonitorResult(
                    monitor: MonitorType.CrossCorrelation,
                    timestamp: DateTime.UtcNow,
                    durationMs: 0,
                    overallSeverity: overall,
                    findings: correlations,
                    server: serverName,
                    metadata: new Dictionary<string, object> { ["type"] = "cross_correlation" }));
            }
        }

        return results;
    }
}
