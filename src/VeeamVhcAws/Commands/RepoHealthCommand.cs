using System.CommandLine;
using VeeamVhcAws.Infrastructure;

namespace VeeamVhcAws.Commands;

public static class RepoHealthCommand
{
    public static Command Create()
    {
        var command = new Command("repo-health", "Run the repository health monitor on all VBR servers");
        var configOption = CommandHelpers.ConfigOption();
        command.AddOption(configOption);

        command.SetHandler((string? config) =>
        {
            RunSingleMonitor(config, "repo_health", "No VBR servers configured for repo-health.");
        }, configOption);

        return command;
    }

    private static void RunSingleMonitor(string? configArg, string monitorName, string noServersMsg)
    {
        try
        {
            var (cfg, servers, pe, dispatcher, state) = CommandHelpers.LoadAndSetup(configArg);
            var results = MonitorRunner.RunAllServers(servers, cfg, pe, monitorFilter: monitorName, findingState: state);
            if (results.Count == 0)
            {
                Console.Error.WriteLine(noServersMsg);
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
    }
}
