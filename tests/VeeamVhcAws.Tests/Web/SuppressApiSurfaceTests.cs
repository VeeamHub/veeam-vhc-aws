using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using VeeamVhcAws.Web;
using Xunit;

namespace VeeamVhcAws.Tests.Web;

// The Alerts and Captures pages suppress through StateService inside the Blazor
// circuit. Nothing calls the old minimal-API routes, so they are attack surface
// with no operator: a state change reachable by plain HTTP. They must stay gone.
public class SuppressApiSurfaceTests : IAsyncLifetime
{
    private const string Active = "alert-1";
    private const string Suppressed = "alert-2";
    private string _dir = "";
    private string _config = "";
    private string _state = "";
    private string _stateBefore = "";
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _dir = Path.Combine(Path.GetTempPath(), "vhc-suppress-api-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _config = Path.Combine(_dir, "veeam-vhc-aws.yaml");
        _state = Path.Combine(_dir, "state.json");
        File.WriteAllText(_config, "global:\n  state_file: ./state.json\n");
        File.WriteAllText(_state, """
            {"findings":{
              "alert-1":{"severity":"warning","resource":"repo","message":"bad"},
              "alert-2":{"severity":"warning","resource":"repo","message":"muted"}},
             "suppressions":{"alert-2":{"suppressed_at":"2026-01-01T00:00:00.0000000Z"}}}
            """);
        _stateBefore = File.ReadAllText(_state);

        // Loopback bind without a token is the default `ui` launch, so no auth
        // gate stands in front of these routes there.
        var port = await StartOnFreePort(new AuthOptions { RequireToken = false });
        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        await WaitUntilReady(_client);
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task CrossSitePost_WithoutAntiforgeryToken_DoesNotSuppress()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/alerts/{Active}/suppress")
        {
            Content = new FormUrlEncodedContent([]),
        };
        request.Headers.TryAddWithoutValidation("Origin", "https://attacker.example");

        var response = await _client.SendAsync(request);

        Assert.False(response.IsSuccessStatusCode, $"cross-site POST returned {(int)response.StatusCode}");
        Assert.Equal(_stateBefore, File.ReadAllText(_state));
    }

    // Each key is one the route would change today: POST suppresses the active
    // alert, DELETE unsuppresses the muted one.
    [Theory]
    [InlineData("POST", Active)]
    [InlineData("DELETE", Suppressed)]
    public async Task SuppressRoutes_AreGone_EvenWithAValidAntiforgeryToken(string method, string key)
    {
        var request = WithAntiforgery(new HttpRequestMessage(new HttpMethod(method), $"/api/alerts/{key}/suppress"));

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(_stateBefore, File.ReadAllText(_state));
    }

    [Fact]
    public async Task Healthz_StaysOpenForProbes()
    {
        var response = await _client.GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private HttpRequestMessage WithAntiforgery(HttpRequestMessage request)
    {
        var antiforgery = _app.Services.GetRequiredService<IAntiforgery>();
        var tokenContext = new DefaultHttpContext { RequestServices = _app.Services };
        var tokens = antiforgery.GetAndStoreTokens(tokenContext);
        request.Headers.TryAddWithoutValidation("Cookie", tokenContext.Response.Headers.SetCookie.ToString().Split(';')[0]);
        request.Headers.TryAddWithoutValidation("RequestVerificationToken", tokens.RequestToken);
        return request;
    }

    // Probing a port and binding it later races with other tests, so retry on a
    // fresh port when Kestrel finds it taken.
    private async Task<int> StartOnFreePort(AuthOptions auth)
    {
        for (var attempt = 0; ; attempt++)
        {
            var port = ProbeFreePort();
            var app = WebHost.Build(new WebHostOptions(_config, "127.0.0.1", port), auth);
            try
            {
                await app.StartAsync();
                _app = app;
                return port;
            }
            catch (IOException) when (attempt < 20)
            {
                await app.DisposeAsync();
            }
        }
    }

    private static int ProbeFreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static async Task WaitUntilReady(HttpClient client)
    {
        for (var i = 0; i < 50; i++)
        {
            try
            {
                if ((await client.GetAsync("/healthz")).IsSuccessStatusCode)
                    return;
            }
            catch (HttpRequestException)
            {
            }
            await Task.Delay(100);
        }
        throw new TimeoutException("admin ui did not answer /healthz");
    }
}
