using System.Text.Json;
using Xunit;
using NSubstitute;
using WireMock.Server;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using VeeamVhcAws.Core.Auth;
using VeeamVhcAws.Core.Clients;
using VeeamVhcAws.Core.Config;
using VeeamVhcAws.Core.Models;
using VeeamVhcAws.Core.Patterns;
using VeeamVhcAws.Core.State;
using VeeamVhcAws.Infrastructure;
using VeeamVhcAws.Monitors;

namespace VeeamVhcAws.Tests.Integration;

public class AdaptiveLookbackTests : IDisposable
{
    private readonly WireMockServer _server;
    private readonly VbrClient _client;

    public AdaptiveLookbackTests()
    {
        _server = WireMockServer.Start();

        // Stub OAuth2 token endpoint
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

        var auth = new VbrAuth(_server.Url!, "admin", "password", verifySsl: false);
        _client = new VbrClient(_server.Url!, auth, verifySsl: false, timeout: 10);
    }

    public void Dispose()
    {
        _server.Stop();
        _server.Dispose();
    }

    private static string MakeSessionsPage(int count, string? createdAfter = null)
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

    // --- Test a: Large session pagination ---
    [Fact]
    public void LargeSessionPaginationFetchesAllPages()
    {
        // Serve 5 full pages of 500, then an empty page
        // WireMock v2 doesn't have the callback ResponseMessage API, so we use
        // a simpler approach: serve full pages always and rely on circuit breaker or
        // count. Instead we set up the scenario to return exactly 5 full pages then stop.

        // We'll serve full 500-item pages. After page 5 (skip=2500), return empty.
        // Use WireMock's Scenario to track state
        for (int page = 0; page < 5; page++)
        {
            var skipValue = (page * 500).ToString();
            _server.Given(
                Request.Create()
                    .WithPath("/api/v1/sessions")
                    .WithParam("skip", skipValue)
                    .UsingGet()
            ).RespondWith(
                Response.Create()
                    .WithStatusCode(200)
                    .WithHeader("Content-Type", "application/json")
                    .WithBody(MakeSessionsPage(500))
            );
        }

        // Page 6 (skip=2500) returns empty
        _server.Given(
            Request.Create()
                .WithPath("/api/v1/sessions")
                .WithParam("skip", "2500")
                .UsingGet()
        ).RespondWith(
            Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{\"data\":[]}")
        );

        var sessions = _client.GetSessions(lookbackHours: 24);

        Assert.Equal(2500, sessions.Count);
    }

    // --- Test b: Adaptive lookback narrows window ---
    [Fact]
    public void AdaptiveLookbackNarrowsWindowAfterSuccess()
    {
        var statePath = Path.Combine(Path.GetTempPath(), $"vhc-adaptive-test-{Guid.NewGuid()}.json");
        try
        {
            var findingState = new FindingState(statePath);

            // Simulate a successful run 10 minutes ago
            findingState.SetLastSuccessTime("test-server", "repo_health", DateTime.UtcNow.AddMinutes(-10));

            // Create monitor with 24h max lookback
            var cfg = new Dictionary<string, object>
            {
                ["repo_health"] = new Dictionary<string, object>
                {
                    ["thresholds"] = new Dictionary<string, object>
                    {
                        ["free_space_warning_pct"] = 15,
                        ["free_space_critical_pct"] = 5,
                    },
                    ["session_lookback_hours"] = 24,
                    ["lookback_overlap_minutes"] = 2,
                }
            };

            var mockClient = Substitute.For<IVbrClient>();
            mockClient.GetRepositoryStates().Returns(new List<Dictionary<string, object>>());
            mockClient.GetSessions(Arg.Any<int>()).Returns(new List<Dictionary<string, object>>());
            mockClient.GetScaleoutRepositories().Returns(new List<Dictionary<string, object>>());

            var ctx = new ServerContext("test-server", "vbr", VbrClient: mockClient);
            var monitor = new RepoHealthMonitor(cfg);
            monitor.Run(ctx, new PatternEngine(new List<ErrorPattern>()), findingState);

            // With 10 min elapsed + 2 min overlap = ~12 min = ceil to 1h minimum
            mockClient.Received(1).GetSessions(Arg.Is<int>(h => h == 1));
        }
        finally
        {
            if (File.Exists(statePath)) File.Delete(statePath);
        }
    }

    // --- Test c: Failed run keeps wide lookback ---
    [Fact]
    public void FailedRunKeepsWideLookback()
    {
        var statePath = Path.Combine(Path.GetTempPath(), $"vhc-adaptive-test-{Guid.NewGuid()}.json");
        try
        {
            var findingState = new FindingState(statePath);

            // No last success time set — first run or previous failure

            var cfg = new Dictionary<string, object>
            {
                ["repo_health"] = new Dictionary<string, object>
                {
                    ["thresholds"] = new Dictionary<string, object>
                    {
                        ["free_space_warning_pct"] = 15,
                        ["free_space_critical_pct"] = 5,
                    },
                    ["session_lookback_hours"] = 24,
                }
            };

            var mockClient = Substitute.For<IVbrClient>();
            mockClient.GetRepositoryStates().Returns(new List<Dictionary<string, object>>());
            mockClient.GetSessions(Arg.Any<int>()).Returns(new List<Dictionary<string, object>>());
            mockClient.GetScaleoutRepositories().Returns(new List<Dictionary<string, object>>());

            var ctx = new ServerContext("test-server", "vbr", VbrClient: mockClient);
            var monitor = new RepoHealthMonitor(cfg);
            monitor.Run(ctx, new PatternEngine(new List<ErrorPattern>()), findingState);

            // No prior success, should use full 24h
            mockClient.Received(1).GetSessions(Arg.Is<int>(h => h == 24));
        }
        finally
        {
            if (File.Exists(statePath)) File.Delete(statePath);
        }
    }

    // --- Test d: Circuit breaker trips ---
    [Fact]
    public void CircuitBreakerStopsAtMaxPages()
    {
        // Serve endless full pages of 50
        _server.Given(
            Request.Create().WithPath("/api/v1/sessions").UsingGet()
        ).RespondWith(
            Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(MakeSessionsPage(50))
        );

        var auth = new VbrAuth(_server.Url!, "admin", "password", verifySsl: false);
        var client = new VbrClient(_server.Url!, auth, verifySsl: false, timeout: 10,
            pageSize: 50, maxPages: 5);

        var sessions = client.GetSessions(lookbackHours: 24);

        // Circuit breaker at 5 pages * 50 per page = 250 sessions max
        Assert.Equal(250, sessions.Count);
    }

    // --- Test e: Adaptive lookback with 3-hour gap ---
    [Fact]
    public void AdaptiveLookbackComputesCorrectHoursForLargerGap()
    {
        var statePath = Path.Combine(Path.GetTempPath(), $"vhc-adaptive-test-{Guid.NewGuid()}.json");
        try
        {
            var findingState = new FindingState(statePath);

            // Simulate a successful run 3 hours ago
            findingState.SetLastSuccessTime("test-server", "retention", DateTime.UtcNow.AddHours(-3));

            var cfg = new Dictionary<string, object>
            {
                ["retention"] = new Dictionary<string, object>
                {
                    ["thresholds"] = new Dictionary<string, object>
                    {
                        ["overage_multiplier"] = 1.5,
                        ["max_age_multiplier"] = 1.5,
                        ["orphan_detection"] = false,
                    },
                    ["session_lookback_hours"] = 24,
                    ["lookback_overlap_minutes"] = 2,
                }
            };

            var mockClient = Substitute.For<IVbrClient>();
            mockClient.GetJobs().Returns(new List<Dictionary<string, object>>());
            mockClient.GetBackups().Returns(new List<Dictionary<string, object>>());
            mockClient.GetRestorePoints(Arg.Any<int>(), Arg.Any<int>()).Returns(new List<Dictionary<string, object>>());
            mockClient.GetSessions(Arg.Any<int>()).Returns(new List<Dictionary<string, object>>());

            var ctx = new ServerContext("test-server", "vbr", VbrClient: mockClient);
            var monitor = new RetentionMonitor(cfg);
            monitor.Run(ctx, new PatternEngine(new List<ErrorPattern>()), findingState);

            // 3 hours elapsed + 2 min overlap = ceil(3 + 2/60) = ceil(3.033) = 4h
            mockClient.Received(1).GetSessions(Arg.Is<int>(h => h == 4));
        }
        finally
        {
            if (File.Exists(statePath)) File.Delete(statePath);
        }
    }

    // --- Test f: Adaptive lookback capped at max ---
    [Fact]
    public void AdaptiveLookbackCappedAtMaxConfig()
    {
        var statePath = Path.Combine(Path.GetTempPath(), $"vhc-adaptive-test-{Guid.NewGuid()}.json");
        try
        {
            var findingState = new FindingState(statePath);

            // Simulate a successful run 48 hours ago (exceeds 24h max)
            findingState.SetLastSuccessTime("test-server", "repo_health", DateTime.UtcNow.AddHours(-48));

            var cfg = new Dictionary<string, object>
            {
                ["repo_health"] = new Dictionary<string, object>
                {
                    ["thresholds"] = new Dictionary<string, object>
                    {
                        ["free_space_warning_pct"] = 15,
                        ["free_space_critical_pct"] = 5,
                    },
                    ["session_lookback_hours"] = 24,
                    ["lookback_overlap_minutes"] = 2,
                }
            };

            var mockClient = Substitute.For<IVbrClient>();
            mockClient.GetRepositoryStates().Returns(new List<Dictionary<string, object>>());
            mockClient.GetSessions(Arg.Any<int>()).Returns(new List<Dictionary<string, object>>());
            mockClient.GetScaleoutRepositories().Returns(new List<Dictionary<string, object>>());

            var ctx = new ServerContext("test-server", "vbr", VbrClient: mockClient);
            var monitor = new RepoHealthMonitor(cfg);
            monitor.Run(ctx, new PatternEngine(new List<ErrorPattern>()), findingState);

            // 48h elapsed but capped at 24h max
            mockClient.Received(1).GetSessions(Arg.Is<int>(h => h == 24));
        }
        finally
        {
            if (File.Exists(statePath)) File.Delete(statePath);
        }
    }

    // --- Test g: MonitorRunner sets last success on zero errors ---
    [Fact]
    public void MonitorRunnerSetsLastSuccessOnZeroErrors()
    {
        var statePath = Path.Combine(Path.GetTempPath(), $"vhc-runner-test-{Guid.NewGuid()}.json");
        try
        {
            var findingState = new FindingState(statePath);

            var mockClient = Substitute.For<IVbrClient>();
            mockClient.GetRepositoryStates().Returns(new List<Dictionary<string, object>>
            {
                new() { ["name"] = "Repo1", ["type"] = "WinLocal", ["capacityGB"] = (long)100, ["freeGB"] = (long)50 }
            });
            mockClient.GetSessions(Arg.Any<int>()).Returns(new List<Dictionary<string, object>>());
            mockClient.GetScaleoutRepositories().Returns(new List<Dictionary<string, object>>());

            var servers = new List<ServerContext>
            {
                new("test-server", "vbr", VbrClient: mockClient)
            };
            var cfg = new Dictionary<string, object>
            {
                ["repo_health"] = new Dictionary<string, object>
                {
                    ["enabled"] = true,
                    ["thresholds"] = new Dictionary<string, object>
                    {
                        ["free_space_warning_pct"] = 15,
                        ["free_space_critical_pct"] = 5,
                    },
                },
                ["retention"] = new Dictionary<string, object> { ["enabled"] = false },
                ["worker_health"] = new Dictionary<string, object> { ["enabled"] = false },
            };

            MonitorRunner.RunAllServers(servers, cfg, new PatternEngine(new List<ErrorPattern>()),
                monitorFilter: "repo_health", findingState: findingState);

            // Should have recorded last success
            var lastSuccess = findingState.GetLastSuccessTime("test-server", "repo_health");
            Assert.NotNull(lastSuccess);
            Assert.True((DateTime.UtcNow - lastSuccess.Value).TotalSeconds < 10);
        }
        finally
        {
            if (File.Exists(statePath)) File.Delete(statePath);
        }
    }

    // --- Test h: MonitorRunner does NOT set last success on errors ---
    [Fact]
    public void MonitorRunnerDoesNotSetLastSuccessOnErrors()
    {
        var statePath = Path.Combine(Path.GetTempPath(), $"vhc-runner-test-{Guid.NewGuid()}.json");
        try
        {
            var findingState = new FindingState(statePath);

            var mockClient = Substitute.For<IVbrClient>();
            mockClient.GetRepositoryStates().Returns(_ => throw new HttpRequestException("Connection refused"));
            mockClient.GetSessions(Arg.Any<int>()).Returns(_ => throw new HttpRequestException("Connection refused"));
            mockClient.GetScaleoutRepositories().Returns(_ => throw new HttpRequestException("Connection refused"));

            var servers = new List<ServerContext>
            {
                new("test-server", "vbr", VbrClient: mockClient)
            };
            var cfg = new Dictionary<string, object>
            {
                ["repo_health"] = new Dictionary<string, object>
                {
                    ["enabled"] = true,
                    ["thresholds"] = new Dictionary<string, object>
                    {
                        ["free_space_warning_pct"] = 15,
                        ["free_space_critical_pct"] = 5,
                    },
                },
                ["retention"] = new Dictionary<string, object> { ["enabled"] = false },
                ["worker_health"] = new Dictionary<string, object> { ["enabled"] = false },
            };

            MonitorRunner.RunAllServers(servers, cfg, new PatternEngine(new List<ErrorPattern>()),
                monitorFilter: "repo_health", findingState: findingState);

            // Should NOT have recorded last success since there were errors
            var lastSuccess = findingState.GetLastSuccessTime("test-server", "repo_health");
            Assert.Null(lastSuccess);
        }
        finally
        {
            if (File.Exists(statePath)) File.Delete(statePath);
        }
    }
}
