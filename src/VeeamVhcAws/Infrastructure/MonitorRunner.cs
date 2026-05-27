using Serilog;
using VeeamVhcAws.Core.Config;
using VeeamVhcAws.Core.Models;
using VeeamVhcAws.Core.Patterns;
using VeeamVhcAws.Core.State;
using VeeamVhcAws.Monitors;
using VeeamVhcAws.Ui;

namespace VeeamVhcAws.Infrastructure;

public static class MonitorRunner
{
    private static readonly ILogger Logger = Log.ForContext(typeof(MonitorRunner));
    private const int ParallelThreshold = 2;

    public static List<MonitorResult> RunAllServers(
        List<ServerContext> servers,
        Dictionary<string, object> config,
        PatternEngine? patternEngine,
        string? monitorFilter = null,
        FindingState? findingState = null,
        IProgress<MonitorProgressEvent>? progress = null)
    {
        var allResults = new List<MonitorResult>();

        if (servers.Count > ParallelThreshold)
        {
            Logger.Information("Running {Count} servers in parallel (threshold={Threshold})",
                servers.Count, ParallelThreshold);
            var tasks = servers.Select(ctx =>
                Task.Run(() => RunMonitorsForServer(ctx, config, patternEngine, monitorFilter, findingState, progress))
            ).ToArray();

            try
            {
                Task.WaitAll(tasks);
                foreach (var task in tasks)
                    allResults.AddRange(task.Result);
            }
            catch (AggregateException ae)
            {
                foreach (var e in ae.InnerExceptions)
                    Logger.Error("Server failed entirely: {Error}", e.Message);

                foreach (var task in tasks)
                {
                    if (task.IsCompletedSuccessfully)
                        allResults.AddRange(task.Result);
                }
            }
        }
        else
        {
            foreach (var ctx in servers)
            {
                var results = RunMonitorsForServer(ctx, config, patternEngine, monitorFilter, findingState, progress);
                allResults.AddRange(results);
            }
        }

        return allResults;
    }

    private static List<MonitorResult> RunMonitorsForServer(
        ServerContext serverCtx,
        Dictionary<string, object> config,
        PatternEngine? patternEngine,
        string? monitorFilter,
        FindingState? findingState,
        IProgress<MonitorProgressEvent>? progress = null)
    {
        var applicable = GetApplicableMonitors(monitorFilter, serverCtx, config);
        if (applicable.Count == 0)
            return new List<MonitorResult>();

        var results = new List<MonitorResult>();
        foreach (var (name, factory) in applicable)
        {
            progress?.Report(new MonitorProgressEvent
            {
                Server = serverCtx.Name,
                Monitor = name,
                Status = MonitorProgressStatus.Running,
            });

            try
            {
                var monitor = factory(config);
                Logger.Information("Starting {Monitor} on server '{Server}'", name, serverCtx.Name);
                var result = monitor.Run(serverCtx, patternEngine, findingState);
                PrefixFindings(result, serverCtx.Name);

                // If a monitor returned errors but severity is OK, escalate
                if (result.Errors.Count > 0 && result.OverallSeverity == Severity.Ok)
                {
                    result.OverallSeverity = Severity.Warning;
                    foreach (var errorMsg in result.Errors)
                    {
                        result.Findings.Insert(0, new Finding(
                            Severity.Warning,
                            $"[{serverCtx.Name}] connection",
                            errorMsg.Length > 200 ? errorMsg[..200] : errorMsg));
                    }
                }

                results.Add(result);
                Logger.Information("{Monitor} on '{Server}' completed: severity={Severity}, findings={Findings}, errors={Errors}, duration={Duration}ms",
                    name, serverCtx.Name, result.OverallSeverity.ToLowerString(),
                    result.Findings.Count, result.Errors.Count, result.DurationMs);

                progress?.Report(new MonitorProgressEvent
                {
                    Server = serverCtx.Name,
                    Monitor = name,
                    Status = MonitorProgressStatus.Completed,
                    Result = result,
                });

                // Track last successful run time for adaptive lookback
                if (result.Errors.Count == 0 && findingState != null)
                {
                    findingState.SetLastSuccessTime(serverCtx.Name, name, DateTime.UtcNow);
                    Logger.Debug("Recorded last success time for {Monitor} on '{Server}'", name, serverCtx.Name);
                }

                // If every non-metric finding is a connection error, the server is unreachable —
                // skip remaining monitors rather than letting each one independently time out.
                if (IsServerUnreachable(result, serverCtx.Name))
                {
                    Logger.Warning("Server '{Server}' appears unreachable — skipping remaining monitors", serverCtx.Name);
                    break;
                }
            }
            catch (Exception e)
            {
                Logger.Error("Monitor {Monitor} failed on server '{Server}': {Error}", name, serverCtx.Name, e.Message);
                var monitorType = MonitorTypeExtensions.ParseMonitorType(name);
                var errorResult = new MonitorResult(
                    monitor: monitorType,
                    timestamp: DateTime.UtcNow,
                    durationMs: 0,
                    overallSeverity: Severity.Error,
                    findings: new List<Finding>
                    {
                        new(Severity.Error, $"[{serverCtx.Name}] connection",
                            $"Server unreachable: {(e.Message.Length > 150 ? e.Message[..150] : e.Message)}")
                    },
                    server: serverCtx.Name,
                    errors: new List<string> { e.Message });
                results.Add(errorResult);

                progress?.Report(new MonitorProgressEvent
                {
                    Server = serverCtx.Name,
                    Monitor = name,
                    Status = MonitorProgressStatus.Failed,
                    Result = errorResult,
                });

                // Hard exception = definitely unreachable; skip remaining monitors.
                Logger.Warning("Server '{Server}' unreachable — skipping remaining monitors", serverCtx.Name);
                break;
            }
        }
        return results;
    }

    private static List<(string Name, Func<Dictionary<string, object>, IMonitor> Factory)> GetApplicableMonitors(
        string? monitorFilter,
        ServerContext serverCtx,
        Dictionary<string, object> config)
    {
        var applicable = new List<(string, Func<Dictionary<string, object>, IMonitor>)>();

        foreach (var (name, factory) in MonitorRegistry.Monitors)
        {
            if (monitorFilter != null && name != monitorFilter)
                continue;

            if (MonitorRegistry.MonitorServerTypes.TryGetValue(name, out var requiredType)
                && requiredType != serverCtx.ServerType)
                continue;

            var monitorCfg = config.GetSection(name);
            if (!monitorCfg.Get("enabled", true))
                continue;

            applicable.Add((name, factory));
        }

        return applicable;
    }

    // Returns true when every non-metric, non-Ok finding is a connection error, meaning
    // no real data was returned and retrying subsequent monitors would only waste time.
    private static bool IsServerUnreachable(MonitorResult result, string serverName)
    {
        if (result.Errors.Count == 0)
            return false;

        var actionableFindings = result.Findings
            .Where(f => f.MetricName == null && f.Severity != Severity.Ok)
            .ToList();

        return actionableFindings.Count > 0
            && actionableFindings.All(f => f.Resource == $"[{serverName}] connection");
    }

    private static void PrefixFindings(MonitorResult result, string serverName)
    {
        foreach (var f in result.Findings)
            f.Resource = $"[{serverName}] {f.Resource}";
        result.Server = serverName;
    }
}
