using System.CommandLine;
using Serilog;
using VeeamVhcAws.Infrastructure;

namespace VeeamVhcAws.Commands;

public static class ServeCommand
{
    private static readonly ILogger Logger = Log.ForContext(typeof(ServeCommand));

    public static Command Create()
    {
        var command = new Command("serve", "Start Prometheus HTTP server and run monitors periodically");
        var configOption = CommandHelpers.ConfigOption();
        var portOption = new Option<int>(new[] { "--port", "-p" }, () => 9100, "Prometheus HTTP server port");
        var intervalOption = new Option<int>(new[] { "--interval", "-i" }, () => 300, "Scrape interval in seconds");

        command.AddOption(configOption);
        command.AddOption(portOption);
        command.AddOption(intervalOption);

        command.SetHandler((string? config, int port, int interval) =>
        {
            var (cfg, servers, pe, dispatcher, state) = CommandHelpers.LoadAndSetup(config);

            // Start Prometheus metrics server
            var metricServer = new Prometheus.MetricServer(port: port);
            metricServer.Start();
            Console.WriteLine($"Starting Prometheus HTTP server on port {port}");
            Console.WriteLine($"Monitoring {servers.Count} servers every {interval}s. Press Ctrl+C to stop.");

            while (true)
            {
                var cycleStart = System.Diagnostics.Stopwatch.StartNew();
                var results = MonitorRunner.RunAllServers(servers, cfg, pe);
                results = CrossCorrelator.Correlate(results);
                CommandHelpers.EmitWithState(dispatcher, results, state);

                var cycleDuration = cycleStart.Elapsed.TotalSeconds;
                Logger.Information("Monitor cycle completed in {Duration:F1}s for {Count} servers",
                    cycleDuration, servers.Count);

                if (cycleDuration > interval * 0.8)
                {
                    Logger.Warning("Monitor cycle ({Duration:F1}s) approaching interval ({Interval}s) — consider increasing interval",
                        cycleDuration, interval);
                }

                var sleepMs = Math.Max(0, (int)((interval - cycleDuration) * 1000));
                Thread.Sleep(sleepMs);
            }
        }, configOption, portOption, intervalOption);

        return command;
    }
}
