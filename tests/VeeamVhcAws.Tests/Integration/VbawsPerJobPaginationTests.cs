using Xunit;
using WireMock.Server;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using VeeamVhcAws.Core.Auth;
using VeeamVhcAws.Core.Clients;

namespace VeeamVhcAws.Tests.Integration;

/// <summary>
/// Issue #16 — deterministic (offline) checks of the VBAWS client's per-job session scoping:
/// GetSessionsForJob must page through limit/skip until a short page, and GetPolicies must parse
/// the data envelope. NOTE: the routes/params exercised here are the ASSUMED contract and still
/// require live-appliance confirmation (PR is tagged needs-live-validation).
/// </summary>
public class VbawsPerJobPaginationTests : IDisposable
{
    private readonly WireMockServer _server;
    private readonly VbawsClient _client;

    public VbawsPerJobPaginationTests()
    {
        _server = WireMockServer.Start();
        _server.Given(Request.Create().WithPath("/api/oauth2/token").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBodyAsJson(new { access_token = "test-token", token_type = "Bearer", expires_in = 900 }));

        var auth = new VbawsAuth(_server.Url!, "user", "pass", verifySsl: false);
        _client = new VbawsClient(_server.Url!, auth, verifySsl: false, timeout: 10, retryCount: 0);
    }

    [Fact]
    public void GetSessionsForJob_PagesAcrossBoundary_UntilShortPage()
    {
        // First page (skip=0) is exactly full (200) → loop must fetch another page.
        var fullPage = Enumerable.Range(0, 200)
            .Select(i => (object)new { id = $"s{i}", type = "BackupSession", status = "Success" }).ToArray();
        _server.Given(Request.Create().WithPath("/api/v1/sessions").UsingGet().WithParam("skip", "0"))
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBodyAsJson(new { data = fullPage }));

        // Second page (skip=200) is short (3) → loop stops after this page.
        var lastPage = Enumerable.Range(200, 3)
            .Select(i => (object)new { id = $"s{i}", type = "BackupSession", status = "Failed" }).ToArray();
        _server.Given(Request.Create().WithPath("/api/v1/sessions").UsingGet().WithParam("skip", "200"))
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBodyAsJson(new { data = lastPage }));

        var sessions = _client.GetSessionsForJob("job-1", DateTime.UtcNow.AddHours(-24), DateTime.UtcNow);

        Assert.Equal(203, sessions.Count); // 200 (page 1) + 3 (page 2)
    }

    [Fact]
    public void GetPolicies_ParsesDataEnvelope()
    {
        _server.Given(Request.Create().WithPath("/api/v1/policies").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBodyAsJson(new { data = new[] { new { id = "p1", name = "policy-one" } } }));

        var policies = _client.GetPolicies();

        Assert.Single(policies);
        Assert.Equal("p1", policies[0]["id"]?.ToString());
    }

    // If the appliance ignores skip/limit and returns the same full page every time, the loop
    // must stop as soon as a page adds no new ids — not re-fetch identically up to maxPages (50).
    [Fact]
    public void GetSessionsForJob_StopsEarly_WhenApiIgnoresSkip()
    {
        // Every /sessions GET returns the SAME 200 rows regardless of skip.
        var samePage = Enumerable.Range(0, 200)
            .Select(i => (object)new { id = $"s{i}", type = "BackupSession", status = "Success" }).ToArray();
        _server.Given(Request.Create().WithPath("/api/v1/sessions").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBodyAsJson(new { data = samePage }));

        var sessions = _client.GetSessionsForJob("job-1", DateTime.UtcNow.AddHours(-24), DateTime.UtcNow);

        Assert.Equal(200, sessions.Count); // deduped to the unique set
        var sessionRequests = _server.LogEntries.Count(e =>
            e.RequestMessage.Path == "/api/v1/sessions" && e.RequestMessage.Method == "GET");
        Assert.Equal(2, sessionRequests); // page 1 (200 new) + page 2 (0 new → stop), not 50
    }

    public void Dispose() => _server.Stop();
}
