using System.CommandLine;
using VeeamVhcAws.Infrastructure;

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

                var results = MonitorRunner.RunAllServers(servers, cfg, pe);
                results = CrossCorrelator.Correlate(results);
                CommandHelpers.EmitWithState(dispatcher, results, state);
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
