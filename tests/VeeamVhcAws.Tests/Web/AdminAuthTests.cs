using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Http;
using VeeamVhcAws.Web;
using Xunit;

namespace VeeamVhcAws.Tests.Web;

public class AdminAuthTests
{
    private const string Token = "0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task Healthz_StaysOpen_AndFrameworkBypass_IsOnlyTheBlazorAssets()
    {
        Assert.Equal(204, await Through("/healthz"));
        Assert.Equal(204, await Through("/_framework/blazor.web.js"));
        Assert.Equal(401, await Through("/_framework/secret.txt"));
        Assert.Equal(401, await Through("/_frameworkfoo"));
        Assert.Equal(401, await Through("/_framework/nested/app.js"));
        Assert.Equal(401, await Through("/_framework/../api/config"));
        Assert.Equal(401, await Through("/api/config"));
        Assert.Equal(401, await Through("/_framework/blazor.web.js", "POST"));
        Assert.Equal(204, await Through("/api/config?t=" + Token));
    }

    [Fact]
    public async Task TokenGatedHost_KeepsHealthzOpen_AndHasNoSuppressApi()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vhc-auth-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var config = Path.Combine(dir, "veeam-vhc-aws.yaml");
        var state = Path.Combine(dir, "state.json");
        File.WriteAllText(config, "global:\n  state_file: ./state.json\n");
        File.WriteAllText(state, """
            {"findings":{"alert-1":{"severity":"warning","resource":"repo","message":"bad"}}}
            """);
        var port = FreePort();
        var app = WebHost.Build(
            new WebHostOptions(config, "127.0.0.1", port),
            new AuthOptions { RequireToken = true, Token = Token });
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        try
        {
            await WaitUntilReady(client);
            var health = await client.GetAsync("/healthz");
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);

            var blocked = await client.GetAsync("/_framework/secret.txt");
            Assert.Equal(HttpStatusCode.Unauthorized, blocked.StatusCode);

            var asset = await client.GetAsync("/_framework/blazor.web.js");
            Assert.NotEqual(HttpStatusCode.Unauthorized, asset.StatusCode);

            // Suppress lives in the Blazor circuit only; even an authenticated POST has no route.
            var bare = await client.PostAsync("/api/alerts/alert-1/suppress?t=" + Token, new StringContent(""));
            Assert.Equal(HttpStatusCode.NotFound, bare.StatusCode);
            Assert.DoesNotContain("suppressed_at", File.ReadAllText(state));
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
            Directory.Delete(dir, recursive: true);
        }
    }

    private static async Task<int> Through(string pathAndQuery, string method = "GET")
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        var q = pathAndQuery.IndexOf('?', StringComparison.Ordinal);
        if (q >= 0)
        {
            ctx.Request.Path = pathAndQuery[..q];
            ctx.Request.QueryString = new QueryString(pathAndQuery[q..]);
        }
        else
        {
            ctx.Request.Path = pathAndQuery;
        }
        ctx.Response.Body = new MemoryStream();
        var mw = new AuthMiddleware(
            inner =>
            {
                inner.Response.StatusCode = 204;
                return Task.CompletedTask;
            },
            new AuthOptions { RequireToken = true, Token = Token });
        await mw.InvokeAsync(ctx);
        return ctx.Response.StatusCode;
    }

    private static async Task WaitUntilReady(HttpClient client)
    {
        for (var i = 0; i < 50; i++)
        {
            try
            {
                var response = await client.GetAsync("/healthz");
                if (response.IsSuccessStatusCode)
                    return;
            }
            catch (HttpRequestException)
            {
            }
            await Task.Delay(100);
        }
        throw new TimeoutException("admin ui did not answer /healthz");
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
