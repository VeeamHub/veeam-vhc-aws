using Xunit;
using WireMock.Server;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using VeeamVhcAws.Core.Auth;
using VeeamVhcAws.Core.Clients;

namespace VeeamVhcAws.Tests.Integration;

/// <summary>
/// The global GET /sessions fetch (the worker-health baseline) must page with Limit/Offset
/// like the per-job fetch does. Stopping after one page hides every session past the first
/// page, which is how deduplicated workloads drop out of the failure-rate math.
/// </summary>
public class VbawsGlobalSessionPagingTests : IDisposable
{
    private readonly WireMockServer _server;
    private readonly VbawsClient _client;

    public VbawsGlobalSessionPagingTests()
    {
        _server = WireMockServer.Start();
        _server.Given(Request.Create().WithPath("/api/oauth2/token").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBodyAsJson(new { access_token = "test-token", token_type = "Bearer", expires_in = 900 }));

        var auth = new VbawsAuth(_server.Url!, "user", "pass", verifySsl: false);
        _client = new VbawsClient(_server.Url!, auth, verifySsl: false, timeout: 10, retryCount: 0);
    }

    private static object[] Page(int start, int count) =>
        Enumerable.Range(start, count)
            .Select(i => (object)new { id = $"g{i}", type = "PolicyBackup", status = "Success" }).ToArray();

    private int SessionRequests() => _server.LogEntries.Count(e =>
        e.RequestMessage.Path == "/api/v1/sessions" && e.RequestMessage.Method == "GET");

    [Fact]
    public void GetSessions_PagesPastTheFirstPage()
    {
        // Any request that is not for Offset=200 gets a full first page.
        _server.Given(Request.Create().WithPath("/api/v1/sessions").UsingGet()).AtPriority(10)
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBodyAsJson(new { data = Page(0, 200) }));
        _server.Given(Request.Create().WithPath("/api/v1/sessions").UsingGet().WithParam("Offset", "200")).AtPriority(1)
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBodyAsJson(new { data = Page(200, 3) }));

        var sessions = _client.GetSessions(DateTime.UtcNow.AddHours(-24), DateTime.UtcNow);

        Assert.Equal(203, sessions.Count);
        Assert.Equal(2, SessionRequests());
    }

    [Fact]
    public void GetSessions_KeepsTheTimeWindowOnEveryPage()
    {
        _server.Given(Request.Create().WithPath("/api/v1/sessions").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBodyAsJson(new { data = Page(0, 5) }));

        _client.GetSessions(DateTime.UtcNow.AddHours(-24), DateTime.UtcNow);

        var request = Assert.Single(_server.LogEntries, e => e.RequestMessage.Path == "/api/v1/sessions").RequestMessage;
        Assert.True(request.Query!.ContainsKey("from"));
        Assert.True(request.Query!.ContainsKey("to"));
    }

    // Same guard as the per-job fetch (fab8955): a server that ignores Offset must not be
    // re-fetched up to the page cap.
    [Fact]
    public void GetSessions_StopsEarly_WhenServerIgnoresOffset()
    {
        _server.Given(Request.Create().WithPath("/api/v1/sessions").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBodyAsJson(new { data = Page(0, 200) }));

        var sessions = _client.GetSessions(DateTime.UtcNow.AddHours(-24), DateTime.UtcNow);

        Assert.Equal(200, sessions.Count);
        Assert.Equal(2, SessionRequests());
    }

    public void Dispose() => _server.Stop();
}
