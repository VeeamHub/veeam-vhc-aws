using System.CommandLine;
using VeeamVhcAws.Core.Config;
using VeeamVhcAws.Infrastructure;
using VeeamVhcAws.Monitors;
using VeeamVhcAws.Ui;

namespace VeeamVhcAws.Commands;

public static class AllCommand
{
    public static Command Create()
    {
        var command = new Command("all", "Run all enabled monitors on all servers, cross-correlate findings");
        var configOption = CommandHelpers.ConfigOption();
        command.AddOption(configOption);

        command.SetHandler((string? config) =>
        {
            try
            {
                var (cfg, servers, pe, dispatcher, state) = CommandHelpers.LoadAndSetup(config);

                if (servers.Count == 0)
                {
                    Console.Error.WriteLine("No servers configured.");
                    Environment.ExitCode = 0;
                    return;
                }

                // Determine if json_stdout is in outputs (if so, avoid writing UI chrome to stdout)
                var outputConfigs = cfg.GetListOfSections("output");
                var hasJsonStdout = outputConfigs.Any(o => o.Get("type", "") == "json_stdout");

                List<Core.Models.MonitorResult> results;

                if (TerminalCapabilities.IsInteractive && !hasJsonStdout)
                {
                    var plannedWork = servers
                        .SelectMany(s => MonitorRegistry.Monitors.Keys
                            .Where(name => !MonitorRegistry.MonitorServerTypes.TryGetValue(name, out var req) || req == s.ServerType)
                            .Select(name => (s.Name, name)))
                        .ToList();

                    var (view, callback) = LiveMonitorView.Start(plannedWork);
                    using (view)
                    {
                        var progressReporter = new Progress<MonitorProgressEvent>(callback);
                        results = MonitorRunner.RunAllServers(servers, cfg, pe, findingState: state, progress: progressReporter);
                    }
                }
                else
                {
                    results = MonitorRunner.RunAllServers(servers, cfg, pe, findingState: state);
                }

                results = CrossCorrelator.Correlate(results);
                CommandHelpers.EmitWithState(dispatcher, results, state);

                // Show results view when interactive and not emitting json to stdout
                if (TerminalCapabilities.IsInteractive && !hasJsonStdout)
                    ResultsView.Render(results);

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
