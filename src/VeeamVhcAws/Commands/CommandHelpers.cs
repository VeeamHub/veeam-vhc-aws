using System.CommandLine;
using Serilog;
using VeeamVhcAws.Core.Config;
using VeeamVhcAws.Core.Logging;
using VeeamVhcAws.Core.Models;
using VeeamVhcAws.Core.Output;
using VeeamVhcAws.Core.Patterns;
using VeeamVhcAws.Core.State;
using VeeamVhcAws.Infrastructure;

namespace VeeamVhcAws.Commands;

public static class CommandHelpers
{
    private static readonly ILogger Logger = Log.ForContext(typeof(CommandHelpers));

    public static Option<string?> ConfigOption() =>
        new(new[] { "--config", "-c" }, "Path to config file");

    public static (Dictionary<string, object> Config, List<ServerContext> Servers,
        PatternEngine? PatternEngine, OutputDispatcher Dispatcher, FindingState State)
        LoadAndSetup(string? configArg)
    {
        var configPath = ConfigLoader.GetConfigPath(configArg);
        var config = ConfigLoader.LoadConfig(configPath);
        LoggingSetup.Setup(config);
        Logger.Information("veeam-vhc-aws starting (PID {ProcessId})", Environment.ProcessId);

        var servers = ServerContextBuilder.BuildAllServers(config);
        var patternEngine = BuildPatternEngine(config);
        var handlers = OutputHandlerFactory.CreateHandlers(config);
        var dispatcher = new OutputDispatcher(handlers);
        var stateFile = config.GetSection("global").Get("state_file", "./veeam-vhc-aws-state.json");
        var configSuppressions = config.GetListOfStrings("suppressions");
        var state = new FindingState(stateFile, configSuppressions);

        Logger.Information("Configured {ServerCount} servers, {HandlerCount} output handlers",
            servers.Count, handlers.Count);

        return (config, servers, patternEngine, dispatcher, state);
    }

    public static PatternEngine? BuildPatternEngine(Dictionary<string, object> config)
    {
        var patternsCfg = config.GetListOfSections("error_patterns");
        if (patternsCfg.Count == 0) return null;
        return PatternEngine.FromConfig(patternsCfg);
    }

    public static void EmitWithState(OutputDispatcher dispatcher, List<MonitorResult> results, FindingState state)
    {
        var (filtered, resolved) = state.ProcessResults(results);

        if (resolved.Count > 0)
            Logger.Information("{Count} findings resolved", resolved.Count);

        dispatcher.Emit(filtered);

        if (resolved.Count > 0)
        {
            var resolvedResult = new MonitorResult(
                monitor: MonitorType.CrossCorrelation,
                timestamp: DateTime.UtcNow,
                durationMs: 0,
                overallSeverity: Severity.Ok,
                findings: resolved,
                server: "all",
                metadata: new Dictionary<string, object> { ["type"] = "resolved" });
            dispatcher.Emit(new[] { resolvedResult });
        }
    }

    public static int WorstExitCode(List<MonitorResult> results)
    {
        int worst = 0;
        foreach (var result in results)
        {
            var code = result.OverallSeverity.ToExitCode();
            if (code > worst) worst = code;
            foreach (var finding in result.Findings)
            {
                code = finding.Severity.ToExitCode();
                if (code > worst) worst = code;
            }
        }
        return worst;
    }
}
