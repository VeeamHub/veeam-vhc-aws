using System.Text.Json;
using Xunit;
using WireMock.Server;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using VeeamVhcAws.Core.Auth;
using VeeamVhcAws.Core.Clients;

namespace VeeamVhcAws.Tests.Integration;

public class PerformanceIntegrationTests : IDisposable
{
    private readonly WireMockServer _server;

    public PerformanceIntegrationTests()
    {
        _server = WireMockServer.Start();
        StubOAuth2Token();
    }

    public void Dispose()
    {
        _server.Stop();
        _server.Dispose();
    }

    private void StubOAuth2Token()
    {
        _server.Given(
            Request.Create().WithPath("/api/oauth2/token").UsingPost()
        ).RespondWith(
            Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(JsonSerializer.Serialize(new
                {
                    access_token = "test-token",
                    token_type = "Bearer",
                    expires_in = 3600
                }))
        );
    }

    private VbrAuth MakeAuth() =>
        new VbrAuth(_server.Url!, "admin", "password", verifySsl: false);

    private static string MakeSessionsPage(int count)
    {
        var sessions = Enumerable.Range(0, count).Select(i => new Dictionary<string, object>
        {
            ["id"] = $"session-{Guid.NewGuid():N}",
            ["sessionType"] = "BackupJob",
            ["name"] = $"Test Job {i}",
            ["creationTime"] = DateTime.UtcNow.AddMinutes(-i).ToString("O"),
            ["result"] = new Dictionary<string, object>
            {
                ["result"] = "Success",
                ["message"] = ""
            }
        }).ToList();

        return JsonSerializer.Serialize(new { data = sessions });
    }

    // Test 1: Sessions endpoint with 500ms delay completes successfully within 2s timeout
    [Fact]
    public void SlowSessionsResponseCompletesWithinTimeout()
    {
        _server.Given(
            Request.Create().WithPath("/api/v1/sessions").UsingGet()
        ).RespondWith(
            Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(MakeSessionsPage(10))
                .WithDelay(TimeSpan.FromMilliseconds(500))
        );

        var client = new VbrClient(_server.Url!, MakeAuth(), verifySsl: false, timeout: 2, retryCount: 0);
        var sessions = client.GetSessions(lookbackHours: 24);

        Assert.Equal(10, sessions.Count);
    }

    // Test 2: Sessions endpoint with 5s delay triggers TaskCanceledException with 2s client timeout
    [Fact]
    public void TimeoutThrowsWhenDelayExceedsClientTimeout()
    {
        _server.Given(
            Request.Create().WithPath("/api/v1/sessions").UsingGet()
        ).RespondWith(
            Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(MakeSessionsPage(10))
                .WithDelay(TimeSpan.FromSeconds(5))
        );

        var client = new VbrClient(_server.Url!, MakeAuth(), verifySsl: false, timeout: 2, retryCount: 0, sessionTimeout: 2);

        Assert.Throws<TaskCanceledException>(() => client.GetSessions(lookbackHours: 24));
    }

    // Test 3: First attempt returns 503 (transient error), retry succeeds on second attempt
    [Fact]
    public void RetrySucceedsAfterTransientFailure()
    {
        const string scenario = "transient-failure";
        const string stateAfterFirst = "after-first";

        // First request: 503 Service Unavailable (transient server error — matches any/initial state)
        _server.Given(
            Request.Create().WithPath("/api/v1/sessions").UsingGet()
        )
            .InScenario(scenario)
            .WillSetStateTo(stateAfterFirst)
            .RespondWith(
                Response.Create()
                    .WithStatusCode(503)
                    .WithBody("Service Unavailable")
            );

        // Second request: responds instantly with data
        _server.Given(
            Request.Create().WithPath("/api/v1/sessions").UsingGet()
        )
            .InScenario(scenario)
            .WhenStateIs(stateAfterFirst)
            .RespondWith(
                Response.Create()
                    .WithStatusCode(200)
                    .WithHeader("Content-Type", "application/json")
                    .WithBody(MakeSessionsPage(7))
            );

        var client = new VbrClient(_server.Url!, MakeAuth(), verifySsl: false,
            timeout: 2, retryCount: 1, retryDelay: 0);

        var sessions = client.GetSessions(lookbackHours: 24);

        Assert.Equal(7, sessions.Count);
    }

    // Test 4: All attempts time out — TaskCanceledException propagates after retry exhaustion
    [Fact]
    public void RetryExhaustionPropagatesException()
    {
        _server.Given(
            Request.Create().WithPath("/api/v1/sessions").UsingGet()
        ).RespondWith(
            Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(MakeSessionsPage(5))
                .WithDelay(TimeSpan.FromSeconds(5))
        );

        var client = new VbrClient(_server.Url!, MakeAuth(), verifySsl: false,
            timeout: 2, retryCount: 1, retryDelay: 1, sessionTimeout: 2);

        Assert.Throws<TaskCanceledException>(() => client.GetSessions(lookbackHours: 24));
    }

    // Test 5: 3 pages with 300ms delay each — all 1500 sessions collected despite cumulative latency
    [Fact]
    public void SlowPaginationCollectsAllPages()
    {
        const int pageSize = 500;

        // Page 1: skip=0 — 500 sessions with 300ms delay
        _server.Given(
            Request.Create()
                .WithPath("/api/v1/sessions")
                .WithParam("skip", "0")
                .UsingGet()
        ).RespondWith(
            Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(MakeSessionsPage(pageSize))
                .WithDelay(TimeSpan.FromMilliseconds(300))
        );

        // Page 2: skip=500 — 500 sessions with 300ms delay
        _server.Given(
            Request.Create()
                .WithPath("/api/v1/sessions")
                .WithParam("skip", "500")
                .UsingGet()
        ).RespondWith(
            Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(MakeSessionsPage(pageSize))
                .WithDelay(TimeSpan.FromMilliseconds(300))
        );

        // Page 3: skip=1000 — 500 sessions with 300ms delay
        _server.Given(
            Request.Create()
                .WithPath("/api/v1/sessions")
                .WithParam("skip", "1000")
                .UsingGet()
        ).RespondWith(
            Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(MakeSessionsPage(pageSize))
                .WithDelay(TimeSpan.FromMilliseconds(300))
        );

        // Page 4: skip=1500 — empty, signals end of pagination
        _server.Given(
            Request.Create()
                .WithPath("/api/v1/sessions")
                .WithParam("skip", "1500")
                .UsingGet()
        ).RespondWith(
            Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{\"data\":[]}")
        );

        var client = new VbrClient(_server.Url!, MakeAuth(), verifySsl: false,
            timeout: 3, retryCount: 0, pageSize: pageSize);

        var sessions = client.GetSessions(lookbackHours: 24);

        Assert.Equal(1500, sessions.Count);
    }
}
