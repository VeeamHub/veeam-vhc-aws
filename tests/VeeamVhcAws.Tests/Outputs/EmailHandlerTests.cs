using Xunit;
using VeeamVhcAws.Core.Models;
using VeeamVhcAws.Outputs;

namespace VeeamVhcAws.Tests.Outputs;

/// <summary>
/// Regression tests for issue #15 — daily summary emails not firing consistently.
/// Root cause: the daily summary was silently skipped on fully-healthy days because the
/// "no findings or errors" gate did not exempt summary emails. A summary must always send.
/// </summary>
public class EmailHandlerTests
{
    private static EmailHandler Handler(string minSeverity = "critical") =>
        new("smtp.test", 587, "from@test.local", new List<string> { "to@test.local" }, minSeverity);

    private static MonitorResult AllHealthy(bool summary = false)
    {
        var m = new MonitorResult(MonitorType.RepoHealth, DateTime.UtcNow, 100, Severity.Ok,
            new List<Finding> { new(Severity.Ok, "repo:demo", "All good") }, server: "s1");
        if (summary) m.Metadata["summary"] = true;
        return m;
    }

    private static MonitorResult WithFinding(Severity sev, bool summary = false)
    {
        var m = new MonitorResult(MonitorType.RepoHealth, DateTime.UtcNow, 100, sev,
            new List<Finding> { new(sev, "repo:demo", "Something to report") }, server: "s1");
        if (summary) m.Metadata["summary"] = true;
        return m;
    }

    // ISC-1 (THE BUG): a daily summary with an all-clear result must still send.
    [Fact]
    public void Summary_AllHealthy_StillSends()
    {
        var send = Handler().ShouldEmit(new[] { AllHealthy(summary: true) }, out var reason);
        Assert.True(send, $"summary should always send; skipped with reason: {reason}");
    }

    // ISC-2 (regression guard): a non-summary all-healthy run must stay suppressed.
    [Fact]
    public void NonSummary_AllHealthy_IsSuppressed()
    {
        var send = Handler().ShouldEmit(new[] { AllHealthy(summary: false) }, out var reason);
        Assert.False(send);
        Assert.NotNull(reason);
    }

    // ISC-3: a summary that has findings sends.
    [Fact]
    public void Summary_WithFindings_Sends()
    {
        var send = Handler().ShouldEmit(new[] { WithFinding(Severity.Warning, summary: true) }, out _);
        Assert.True(send);
    }

    // ISC-4: a non-summary alert run with a critical finding sends (threshold met).
    [Fact]
    public void NonSummary_CriticalFinding_Sends()
    {
        var send = Handler().ShouldEmit(new[] { WithFinding(Severity.Critical, summary: false) }, out _);
        Assert.True(send);
    }

    // A summary with zero results at all is still a valid all-clear send.
    [Fact]
    public void Summary_NoResults_StillSends()
    {
        var summaryOnly = new MonitorResult(MonitorType.CrossCorrelation, DateTime.UtcNow, 0, Severity.Ok,
            new List<Finding>(), server: "s1");
        summaryOnly.Metadata["summary"] = true;
        var send = Handler().ShouldEmit(new[] { summaryOnly }, out var reason);
        Assert.True(send, $"empty summary should send an all-clear; skipped with: {reason}");
    }
}
