using Xunit;
using WireMock.Server;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using VeeamVhcAws.Core.Auth;
using VeeamVhcAws.Core.Clients;

namespace VeeamVhcAws.Tests.Integration;

/// <summary>
/// Issue #16 — deterministic (offline) checks of the VBAWS client's per-policy session scoping
/// against the confirmed VBAWS 1.8-rev0 contract: GET /sessions filtered by PolicyId, paged with
/// Limit/Offset; policies enumerated across per-workload routes. (The ~5k cap itself is
/// undocumented empirical behavior — only whether it resets per PolicyId needs live confirmation.)
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

    // Confirms the client scopes by PolicyId and pages with Offset until a short page.
    // The stubs match on PolicyId + Offset, so the test fails (404) if the client sends the wrong
    // param names — i.e. it validates the corrected contract, not just the loop.
    [Fact]
    public void GetSessionsForJob_ScopesByPolicyId_PagesByOffset()
    {
        var fullPage = Enumerable.Range(0, 200)
            .Select(i => (object)new { id = $"s{i}", type = "BackupSession", status = "Success" }).ToArray();
        _server.Given(Request.Create().WithPath("/api/v1/sessions").UsingGet()
                .WithParam("PolicyId", "policy-1").WithParam("Offset", "0"))
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBodyAsJson(new { data = fullPage }));

        var lastPage = Enumerable.Range(200, 3)
            .Select(i => (object)new { id = $"s{i}", type = "BackupSession", status = "Failed" }).ToArray();
        _server.Given(Request.Create().WithPath("/api/v1/sessions").UsingGet()
                .WithParam("PolicyId", "policy-1").WithParam("Offset", "200"))
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBodyAsJson(new { data = lastPage }));

        var sessions = _client.GetSessionsForJob("policy-1", DateTime.UtcNow.AddHours(-24), DateTime.UtcNow);

        Assert.Equal(203, sessions.Count); // 200 (Offset 0) + 3 (Offset 200)
    }

    // GetPolicies aggregates across the per-workload routes (no single /policies collection).
    // Stubs two workloads; the other six 404 and are skipped.
    [Fact]
    public void GetPolicies_AggregatesAcrossWorkloadRoutes()
    {
        _server.Given(Request.Create().WithPath("/api/v1/virtualMachines/policies").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBodyAsJson(new { data = new[] { new { id = "ec2-1", name = "ec2-policy" } } }));
        _server.Given(Request.Create().WithPath("/api/v1/rds/policies").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBodyAsJson(new { data = new[] { new { id = "rds-1", name = "rds-policy" } } }));

        var policies = _client.GetPolicies();

        Assert.Equal(2, policies.Count);
        Assert.Contains(policies, p => p["id"]?.ToString() == "ec2-1");
        Assert.Contains(policies, p => p["id"]?.ToString() == "rds-1");
    }

    // If a server ignores Offset and returns the same rows every page, the loop must stop as soon
    // as a page adds no new ids — not re-fetch identically up to maxPages (50).
    [Fact]
    public void GetSessionsForJob_StopsEarly_WhenServerIgnoresOffset()
    {
        var samePage = Enumerable.Range(0, 200)
            .Select(i => (object)new { id = $"s{i}", type = "BackupSession", status = "Success" }).ToArray();
        _server.Given(Request.Create().WithPath("/api/v1/sessions").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBodyAsJson(new { data = samePage }));

        var sessions = _client.GetSessionsForJob("policy-1", DateTime.UtcNow.AddHours(-24), DateTime.UtcNow);

        Assert.Equal(200, sessions.Count); // deduped to the unique set
        var sessionRequests = _server.LogEntries.Count(e =>
            e.RequestMessage.Path == "/api/v1/sessions" && e.RequestMessage.Method == "GET");
        Assert.Equal(2, sessionRequests); // page 1 (200 new) + page 2 (0 new → stop), not 50
    }

    public void Dispose() => _server.Stop();
}
