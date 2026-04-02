using Serilog;
using VhcMonitor.Core.Config;
using VhcMonitor.Core.Models;
using VhcMonitor.Core.Patterns;
using VhcMonitor.Monitors;

namespace VhcMonitor.Infrastructure;

public static class MonitorRunner
{
    private static readonly ILogger Logger = Log.ForContext(typeof(MonitorRunner));
    private const int ParallelThreshold = 2;

    public static List<MonitorResult> RunAllServers(
        List<ServerContext> servers,
        Dictionary<string, object> config,
        PatternEngine? patternEngine,
        string? monitorFilter = null)
    {
        var allResults = new List<MonitorResult>();

        if (servers.Count > ParallelThreshold)
        {
            Logger.Information("Running {Count} servers in parallel (threshold={Threshold})",
                servers.Count, ParallelThreshold);
            var maxWorkers = Math.Min(servers.Count, 20);
            var tasks = servers.Select(ctx =>
                Task.Run(() => RunMonitorsForServer(ctx, config, patternEngine, monitorFilter))
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
                var results = RunMonitorsForServer(ctx, config, patternEngine, monitorFilter);
                allResults.AddRange(results);
            }
        }

        return allResults;
    }

    private static List<MonitorResult> RunMonitorsForServer(
        ServerContext serverCtx,
        Dictionary<string, object> config,
        PatternEngine? patternEngine,
        string? monitorFilter)
    {
        var applicable = GetApplicableMonitors(monitorFilter, serverCtx, config);
        if (applicable.Count == 0)
            return new List<MonitorResult>();

        var results = new List<MonitorResult>();
        foreach (var (name, factory) in applicable)
        {
            try
            {
                var monitor = factory(config);
                Logger.Information("Starting {Monitor} on server '{Server}'", name, serverCtx.Name);
                var result = monitor.Run(serverCtx, patternEngine);
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
            }
            catch (Exception e)
            {
                Logger.Error("Monitor {Monitor} failed on server '{Server}': {Error}", name, serverCtx.Name, e.Message);
                var monitorType = MonitorTypeExtensions.ParseMonitorType(name);
                results.Add(new MonitorResult(
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
                    errors: new List<string> { e.Message }));
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

    private static void PrefixFindings(MonitorResult result, string serverName)
    {
        foreach (var f in result.Findings)
            f.Resource = $"[{serverName}] {f.Resource}";
        result.Server = serverName;
    }
}
