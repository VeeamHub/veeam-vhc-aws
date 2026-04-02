using System.CommandLine;
using VhcMonitor.Core.Config;
using VhcMonitor.Core.Logging;
using VhcMonitor.Core.Output;
using VhcMonitor.Infrastructure;

namespace VhcMonitor.Commands;

public static class SummaryCommand
{
    public static Command Create()
    {
        var command = new Command("summary", "Run all monitors and emit a full daily summary — all findings, no deduplication");
        var configOption = CommandHelpers.ConfigOption();
        command.AddOption(configOption);

        command.SetHandler((string? config) =>
        {
            try
            {
                var (cfg, servers, pe, _, _) = CommandHelpers.LoadAndSetup(config);

                var summaryCfg = cfg.GetSection("daily_summary");
                if (!summaryCfg.Get("enabled", true))
                {
                    Console.Error.WriteLine("Daily summary is disabled in config.");
                    Environment.ExitCode = 0;
                    return;
                }

                if (servers.Count == 0)
                {
                    Console.Error.WriteLine("No servers configured.");
                    Environment.ExitCode = 0;
                    return;
                }

                var results = MonitorRunner.RunAllServers(servers, cfg, pe);
                results = CrossCorrelator.Correlate(results);

                // Mark all results as summary mode
                foreach (var result in results)
                    result.Metadata["summary"] = true;

                // Use daily_summary.output if specified, else fall back to main output config
                var summaryOutput = summaryCfg.GetListOfSections("output");
                List<IOutputHandler> handlers;
                if (summaryOutput.Count > 0)
                    handlers = OutputHandlerFactory.CreateHandlers(new Dictionary<string, object> { ["output"] = summaryOutput });
                else
                    handlers = OutputHandlerFactory.CreateHandlers(cfg);

                var dispatcher = new OutputDispatcher(handlers);
                dispatcher.Emit(results);

                Environment.ExitCode = CommandHelpers.WorstExitCode(results);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"ERROR: Startup failed: {e.Message}");
                Environment.ExitCode = 3;
            }
        }, configOption);

        return command;
    }
}
