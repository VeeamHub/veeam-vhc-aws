using System.CommandLine;
using VeeamVhcAws.Core.Config;
using VeeamVhcAws.Core.Logging;
using VeeamVhcAws.Infrastructure;
using VeeamVhcAws.Ui;

namespace VeeamVhcAws.Commands;

public static class TestConnectionCommand
{
    public static Command Create()
    {
        var command = new Command("test-connection", "Test connectivity to all configured servers");
        var configOption = CommandHelpers.ConfigOption();
        command.AddOption(configOption);

        command.SetHandler((string? config) =>
        {
            var configPath = ConfigLoader.GetConfigPath(config);
            var cfg = ConfigLoader.LoadConfig(configPath);
            LoggingSetup.Setup(cfg);
            var servers = ServerContextBuilder.BuildAllServers(cfg);
            var outputConfigs = cfg.GetListOfSections("output");

            var results = ConnectionTester.RunAll(servers, outputConfigs);
            ConnectionTester.RenderResults(results);
        }, configOption);

        return command;
    }
}
