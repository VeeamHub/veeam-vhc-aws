using System.Net;
using System.Net.Sockets;
using VeeamVhcAws.Commands;
using VeeamVhcAws.Web;
using Xunit;

namespace VeeamVhcAws.Tests.Commands;

[Collection("console")]
public class UiPortDetectionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunningAdminUi_IsRecognisedAsOurs_WithOrWithoutTokenAuth(bool requireToken)
    {
        var dir = Path.Combine(Path.GetTempPath(), "vhc-port-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var config = Path.Combine(dir, "veeam-vhc-aws.yaml");
        File.WriteAllText(config, "global:\n  state_file: ./state.json\n");
        var port = FreePort();
        var app = WebHost.Build(
            new WebHostOptions(config, "127.0.0.1", port),
            new AuthOptions { RequireToken = requireToken, Token = requireToken ? "0123456789abcdef0123456789abcdef" : "" });
        await app.StartAsync();
        try
        {
            // A token-protected instance answers the anonymous probe with 401 plus the marker, so the
            // second launch knows to open the browser with the token instead of landing on a 401 page.
            var expected = requireToken ? UiCommand.PortState.OursUiTokenRequired : UiCommand.PortState.OursUi;
            Assert.Equal(expected, await UiCommand.DetectPortAsync(port, Timeout));
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task UnrelatedListener_IsForeign()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            // Accept and close immediately: connect succeeds, the HTTP probe fails, so the port is not ours.
            var accept = Task.Run(async () =>
            {
                using var client = await listener.AcceptTcpClientAsync();
            });
            Assert.Equal(UiCommand.PortState.Foreign, await UiCommand.DetectPortAsync(port, Timeout));
            await accept;
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task HttpServerWithoutTheMarker_IsForeign()
    {
        using var http = new HttpListener();
        var port = FreePort();
        http.Prefixes.Add($"http://127.0.0.1:{port}/");
        http.Start();
        var serve = Task.Run(async () =>
        {
            var ctx = await http.GetContextAsync();
            ctx.Response.StatusCode = 200;
            ctx.Response.Close();
        });
        try
        {
            Assert.Equal(UiCommand.PortState.Foreign, await UiCommand.DetectPortAsync(port, Timeout));
            await serve;
        }
        finally
        {
            http.Close();
        }
    }

    [Fact]
    public async Task UnusedPort_IsFree()
    {
        Assert.Equal(UiCommand.PortState.Free, await UiCommand.DetectPortAsync(FreePort(), Timeout));
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
