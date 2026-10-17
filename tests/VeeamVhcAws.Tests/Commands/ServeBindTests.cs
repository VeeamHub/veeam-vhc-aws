using System.CommandLine;
using System.CommandLine.Parsing;
using System.Net;
using System.Net.Sockets;
using VeeamVhcAws.Commands;
using Xunit;

namespace VeeamVhcAws.Tests.Commands;

[Collection("console")]
public class ServeBindTests
{
    [Fact]
    public void Serve_ListensOnLoopbackUnlessBindIsSet()
    {
        var command = ServeCommand.Create();
        var bind = Assert.IsAssignableFrom<Option<string>>(
            command.Options.Single(o => o.Aliases.Contains("--bind")));

        var omitted = command.Parse("--port 9100");
        Assert.Equal("127.0.0.1", omitted.GetValueForOption(bind));

        var chosen = command.Parse("--bind 10.1.2.3 --port 9100");
        Assert.Equal("10.1.2.3", chosen.GetValueForOption(bind));

        var help = new StringWriter();
        var previous = Console.Out;
        try
        {
            Console.SetOut(help);
            Assert.Equal(0, command.Invoke("--help"));
        }
        finally
        {
            Console.SetOut(previous);
        }

        var text = help.ToString();
        Assert.Contains("--bind", text);
        Assert.Contains("127.0.0.1", text);
        Assert.Contains("non-loopback", text);
    }

    [Fact]
    public async Task LoopbackMetricServer_AcceptsLocalhostAndNoOtherLocalAddress()
    {
        var port = FreePort();
        var server = new Prometheus.MetricServer(hostname: "127.0.0.1", port: port);
        server.Start();
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var response = await client.GetAsync($"http://127.0.0.1:{port}/metrics/");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var hexPort = port.ToString("X4");
            var rows = File.ReadAllLines("/proc/net/tcp")
                .Where(line => line.Contains(":" + hexPort, StringComparison.OrdinalIgnoreCase))
                .ToList();
            Assert.NotEmpty(rows);
            Assert.All(rows, row => Assert.Contains("0100007F:" + hexPort, row, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            server.Stop();
        }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
