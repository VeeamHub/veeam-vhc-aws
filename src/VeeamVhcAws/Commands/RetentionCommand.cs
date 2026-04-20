using System.CommandLine;
using VeeamVhcAws.Infrastructure;

namespace VeeamVhcAws.Commands;

public static class RetentionCommand
{
    public static Command Create()
    {
        var command = new Command("retention", "Run the retention monitor on all VBR servers");
        var configOption = CommandHelpers.ConfigOption();
        command.AddOption(configOption);

        command.SetHandler((string? config) =>
        {
            try
            {
                var (cfg, servers, pe, dispatcher, state) = CommandHelpers.LoadAndSetup(config);
                var results = MonitorRunner.RunAllServers(servers, cfg, pe, monitorFilter: "retention", findingState: state);
                if (results.Count == 0)
                {
                    Console.Error.WriteLine("No VBR servers configured for retention.");
                    Environment.ExitCode = 0;
                    return;
                }
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
