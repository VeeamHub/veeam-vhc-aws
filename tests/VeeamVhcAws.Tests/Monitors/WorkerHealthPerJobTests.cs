using Xunit;
using NSubstitute;
using VeeamVhcAws.Core.Clients;
using VeeamVhcAws.Core.Models;
using VeeamVhcAws.Core.Patterns;
using VeeamVhcAws.Infrastructure;
using VeeamVhcAws.Monitors;

namespace VeeamVhcAws.Tests.Monitors;

/// <summary>
/// Issue #16 — the global VBAWS /sessions fetch is recency-capped and silently drops sessions for
/// low-frequency policies. Per-policy session scoping recovers the complete picture. These tests
/// prove the recovery, the graceful fallback, and that coverage never shrinks — all offline.
/// </summary>
public class WorkerHealthPerJobTests
{
    private static Dictionary<string, object> Config(bool perJob) => new()
    {
        ["worker_health"] = new Dictionary<string, object>
        {
            ["lookback_hours"] = 24,
            ["per_job_sessions"] = perJob,
            ["thresholds"] = new Dictionary<string, object>
            {
                ["session_failure_rate_warning"] = 0.1,
                ["session_failure_rate_critical"] = 0.3,
                ["recurring_failure_threshold"] = 3,
            },
        }
    };

    private static Dictionary<string, object> Session(string status, string name, string id) => new()
    {
        ["type"] = "BackupSession",
        ["status"] = status,
        ["name"] = name,
        ["id"] = id,
        ["result"] = new Dictionary<string, object> { ["message"] = "timeout" },
    };

    // Global fetch is capped: returns only a healthy visible policy, NOT the failed low-frequency one.
    // Per-job fetch for the hidden policy returns its failed session.
    private static IVbawsClient ClientWithHiddenFailure()
    {
        var client = Substitute.For<IVbawsClient>();
        client.GetSessions(Arg.Any<DateTime>(), Arg.Any<DateTime>())
            .Returns(new List<Dictionary<string, object>> { Session("Success", "visible-policy", "s1") });
        client.GetPolicies().Returns(new List<Dictionary<string, object>>
        {
            new() { ["id"] = "p-visible" },
            new() { ["id"] = "p-hidden" },
        });
        client.GetSessionsForJob("p-visible", Arg.Any<DateTime>(), Arg.Any<DateTime>())
            .Returns(new List<Dictionary<string, object>>());
        client.GetSessionsForJob("p-hidden", Arg.Any<DateTime>(), Arg.Any<DateTime>())
            .Returns(new List<Dictionary<string, object>> { Session("Failed", "hidden-policy", "s2") });
        return client;
    }

    // ISC-3: with per-job scoping the failed policy hidden by the cap now produces a finding.
    [Fact]
    public void PerJob_SurfacesFailedPolicyHiddenByGlobalCap()
    {
        var ctx = new ServerContext("test-server", "vbaws", VbawsClient: ClientWithHiddenFailure());
        var result = new WorkerHealthMonitor(Config(perJob: true))
            .Run(ctx, new PatternEngine(new List<ErrorPattern>()));
        Assert.Contains(result.Findings, f => f.Resource == "failed:hidden-policy");
    }

    // Control: with per-job scoping OFF, the global cap hides the failed policy (no finding).
    // This is the bug the fix closes.
    [Fact]
    public void GlobalOnly_MissesFailedPolicy_WhenPerJobDisabled()
    {
        var ctx = new ServerContext("test-server", "vbaws", VbawsClient: ClientWithHiddenFailure());
        var result = new WorkerHealthMonitor(Config(perJob: false))
            .Run(ctx, new PatternEngine(new List<ErrorPattern>()));
        Assert.DoesNotContain(result.Findings, f => f.Resource == "failed:hidden-policy");
    }

    // ISC-4: GetPolicies failure degrades to the global set — no throw, error recorded, run completes.
    [Fact]
    public void GetPoliciesFailure_FallsBackToGlobal_AndRecordsError()
    {
        var client = Substitute.For<IVbawsClient>();
        client.GetSessions(Arg.Any<DateTime>(), Arg.Any<DateTime>())
            .Returns(new List<Dictionary<string, object>> { Session("Success", "visible", "s1") });
        client.GetPolicies().Returns<List<Dictionary<string, object>>>(_ => throw new Exception("boom"));
        var ctx = new ServerContext("test-server", "vbaws", VbawsClient: client);

        var result = new WorkerHealthMonitor(Config(perJob: true))
            .Run(ctx, new PatternEngine(new List<ErrorPattern>()));

        Assert.Contains(result.Errors, e => e.Contains("Per-job session scoping skipped"));
    }

    // ISC-2: merged coverage never shrinks — a session only in the global set survives the union.
    [Fact]
    public void Merge_NeverDropsGlobalOnlySessions()
    {
        var client = Substitute.For<IVbawsClient>();
        client.GetSessions(Arg.Any<DateTime>(), Arg.Any<DateTime>())
            .Returns(new List<Dictionary<string, object>> { Session("Failed", "global-only-policy", "g1") });
        client.GetPolicies().Returns(new List<Dictionary<string, object>> { new() { ["id"] = "p1" } });
        client.GetSessionsForJob("p1", Arg.Any<DateTime>(), Arg.Any<DateTime>())
            .Returns(new List<Dictionary<string, object>>());
        var ctx = new ServerContext("test-server", "vbaws", VbawsClient: client);

        var result = new WorkerHealthMonitor(Config(perJob: true))
            .Run(ctx, new PatternEngine(new List<ErrorPattern>()));

        Assert.Contains(result.Findings, f => f.Resource == "failed:global-only-policy");
    }
}
