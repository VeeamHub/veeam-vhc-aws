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

    private static Dictionary<string, object> Session(string status, string name, string id, string? creationTime = null)
    {
        var s = new Dictionary<string, object>
        {
            ["type"] = "BackupSession",
            ["status"] = status,
            ["name"] = name,
            ["result"] = new Dictionary<string, object> { ["message"] = "timeout" },
        };
        if (id.Length > 0) s["id"] = id;
        if (creationTime != null) s["creationTime"] = creationTime;
        return s;
    }

    private static double? BackupSessionTotal(MonitorResult r) => r.Findings
        .Where(f => f.MetricName == "veeam_vbaws_session_total")
        .Select(f => f.MetricValue)
        .FirstOrDefault();

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

    // A session with the SAME id in both the global and per-job sets must be counted once
    // (double-counting would inflate the failure-rate denominator).
    [Fact]
    public void Merge_SameId_InBothSets_CountedOnce()
    {
        var client = Substitute.For<IVbawsClient>();
        client.GetSessions(Arg.Any<DateTime>(), Arg.Any<DateTime>())
            .Returns(new List<Dictionary<string, object>> { Session("Failed", "P", "dup") });
        client.GetPolicies().Returns(new List<Dictionary<string, object>> { new() { ["id"] = "p1" } });
        client.GetSessionsForJob("p1", Arg.Any<DateTime>(), Arg.Any<DateTime>())
            .Returns(new List<Dictionary<string, object>> { Session("Failed", "P", "dup") }); // same id
        var ctx = new ServerContext("test-server", "vbaws", VbawsClient: client);

        var result = new WorkerHealthMonitor(Config(perJob: true))
            .Run(ctx, new PatternEngine(new List<ErrorPattern>()));

        Assert.Equal(1d, BackupSessionTotal(result));
    }

    // An id-less session returned by both fetches must dedup by composite key, not double-count.
    [Fact]
    public void Merge_IdLessSession_InBothSets_NotDoubleCounted()
    {
        var client = Substitute.For<IVbawsClient>();
        client.GetSessions(Arg.Any<DateTime>(), Arg.Any<DateTime>())
            .Returns(new List<Dictionary<string, object>> { Session("Failed", "P", "") });
        client.GetPolicies().Returns(new List<Dictionary<string, object>> { new() { ["id"] = "p1" } });
        client.GetSessionsForJob("p1", Arg.Any<DateTime>(), Arg.Any<DateTime>())
            .Returns(new List<Dictionary<string, object>> { Session("Failed", "P", "") }); // same id-less session
        var ctx = new ServerContext("test-server", "vbaws", VbawsClient: client);

        var result = new WorkerHealthMonitor(Config(perJob: true))
            .Run(ctx, new PatternEngine(new List<ErrorPattern>()));

        Assert.Equal(1d, BackupSessionTotal(result));
    }

    // A per-job fetch that throws for SOME policies must still merge the survivors and record an error.
    [Fact]
    public void PerJob_PartialFailure_MergesSurvivors_AndRecordsError()
    {
        var client = Substitute.For<IVbawsClient>();
        client.GetSessions(Arg.Any<DateTime>(), Arg.Any<DateTime>()).Returns(new List<Dictionary<string, object>>());
        client.GetPolicies().Returns(new List<Dictionary<string, object>>
        {
            new() { ["id"] = "p1" }, new() { ["id"] = "p2" }, new() { ["id"] = "p3" },
        });
        client.GetSessionsForJob("p1", Arg.Any<DateTime>(), Arg.Any<DateTime>())
            .Returns(new List<Dictionary<string, object>> { Session("Failed", "A", "a1") });
        client.GetSessionsForJob("p2", Arg.Any<DateTime>(), Arg.Any<DateTime>())
            .Returns<List<Dictionary<string, object>>>(_ => throw new Exception("boom-p2"));
        client.GetSessionsForJob("p3", Arg.Any<DateTime>(), Arg.Any<DateTime>())
            .Returns(new List<Dictionary<string, object>> { Session("Failed", "C", "c1") });
        var ctx = new ServerContext("test-server", "vbaws", VbawsClient: client);

        var result = new WorkerHealthMonitor(Config(perJob: true))
            .Run(ctx, new PatternEngine(new List<ErrorPattern>()));

        Assert.Contains(result.Findings, f => f.Resource == "failed:A");
        Assert.Contains(result.Findings, f => f.Resource == "failed:C");
        Assert.Contains(result.Errors, e => e.Contains("p2"));
    }

    // Concurrency under real fan-out: all policies' sessions must survive (no lost writes / gate drops).
    [Fact]
    public void PerJob_ManyPolicies_CollectsAll()
    {
        var client = Substitute.For<IVbawsClient>();
        client.GetSessions(Arg.Any<DateTime>(), Arg.Any<DateTime>()).Returns(new List<Dictionary<string, object>>());
        var policies = new List<Dictionary<string, object>>();
        for (int i = 0; i < 50; i++)
        {
            var id = $"p{i}";
            policies.Add(new Dictionary<string, object> { ["id"] = id });
            client.GetSessionsForJob(id, Arg.Any<DateTime>(), Arg.Any<DateTime>())
                .Returns(new List<Dictionary<string, object>> { Session("Failed", $"policy-{i}", $"s{i}") });
        }
        client.GetPolicies().Returns(policies);
        var ctx = new ServerContext("test-server", "vbaws", VbawsClient: client);

        var result = new WorkerHealthMonitor(Config(perJob: true))
            .Run(ctx, new PatternEngine(new List<ErrorPattern>()));

        for (int i = 0; i < 50; i++)
            Assert.Contains(result.Findings, f => f.Resource == $"failed:policy-{i}");
    }

    // Exceeding the per-job cap must be surfaced as an error, not silently truncated.
    [Fact]
    public void PerJob_ExceedingCap_RecordsError()
    {
        var client = Substitute.For<IVbawsClient>();
        client.GetSessions(Arg.Any<DateTime>(), Arg.Any<DateTime>()).Returns(new List<Dictionary<string, object>>());
        var policies = new List<Dictionary<string, object>>();
        for (int i = 0; i < 201; i++)
            policies.Add(new Dictionary<string, object> { ["id"] = $"p{i}" });
        client.GetPolicies().Returns(policies);
        client.GetSessionsForJob(Arg.Any<string>(), Arg.Any<DateTime>(), Arg.Any<DateTime>())
            .Returns(new List<Dictionary<string, object>>());
        var ctx = new ServerContext("test-server", "vbaws", VbawsClient: client);

        var result = new WorkerHealthMonitor(Config(perJob: true))
            .Run(ctx, new PatternEngine(new List<ErrorPattern>()));

        Assert.Contains(result.Errors, e => e.Contains("capped at 200"));
    }
}
