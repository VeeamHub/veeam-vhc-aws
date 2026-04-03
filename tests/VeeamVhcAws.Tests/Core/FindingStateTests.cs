using Xunit;
using VeeamVhcAws.Core.Models;
using VeeamVhcAws.Core.State;

namespace VeeamVhcAws.Tests.Core;

public class FindingStateTests
{
    private string GetTempStatePath() =>
        Path.Combine(Path.GetTempPath(), $"vhc-test-state-{Guid.NewGuid()}.json");

    [Fact]
    public void NewFindingsAreReported()
    {
        var statePath = GetTempStatePath();
        try
        {
            var state = new FindingState(statePath);
            var results = new List<MonitorResult>
            {
                new(MonitorType.RepoHealth, DateTime.UtcNow, 100, Severity.Warning,
                    new List<Finding>
                    {
                        new(Severity.Warning, "repo:Test", "Low space")
                    }, server: "server1")
            };

            var (filtered, resolved) = state.ProcessResults(results);

            Assert.Single(filtered);
            Assert.Single(filtered[0].Findings);
            Assert.Equal("Low space", filtered[0].Findings[0].Message);
            Assert.Empty(resolved);
        }
        finally
        {
            if (File.Exists(statePath)) File.Delete(statePath);
        }
    }

    [Fact]
    public void ExistingFindingsMarkedSeenBefore()
    {
        var statePath = GetTempStatePath();
        try
        {
            var state = new FindingState(statePath);
            var finding = new Finding(Severity.Warning, "repo:Test", "Low space");
            var results = new List<MonitorResult>
            {
                new(MonitorType.RepoHealth, DateTime.UtcNow, 100, Severity.Warning,
                    new List<Finding> { finding }, server: "server1")
            };

            state.ProcessResults(results);

            // Run again — same finding
            var state2 = new FindingState(statePath);
            var finding2 = new Finding(Severity.Warning, "repo:Test", "Low space");
            var results2 = new List<MonitorResult>
            {
                new(MonitorType.RepoHealth, DateTime.UtcNow, 100, Severity.Warning,
                    new List<Finding> { finding2 }, server: "server1")
            };

            var (filtered, resolved) = state2.ProcessResults(results2);

            Assert.Single(filtered);
            Assert.Single(filtered[0].Findings);
            Assert.True(filtered[0].Findings[0].Details.ContainsKey("_seen_before"));
            Assert.Empty(resolved);
        }
        finally
        {
            if (File.Exists(statePath)) File.Delete(statePath);
        }
    }

    [Fact]
    public void ResolvedFindingsDetected()
    {
        var statePath = GetTempStatePath();
        try
        {
            var state = new FindingState(statePath);
            var results = new List<MonitorResult>
            {
                new(MonitorType.RepoHealth, DateTime.UtcNow, 100, Severity.Warning,
                    new List<Finding>
                    {
                        new(Severity.Warning, "repo:Test", "Low space")
                    }, server: "server1")
            };
            state.ProcessResults(results);

            // Run again with empty findings (problem resolved)
            var state2 = new FindingState(statePath);
            var results2 = new List<MonitorResult>
            {
                new(MonitorType.RepoHealth, DateTime.UtcNow, 100, Severity.Ok,
                    new List<Finding>(), server: "server1")
            };
            var (_, resolved) = state2.ProcessResults(results2);

            Assert.Single(resolved);
            Assert.StartsWith("RESOLVED:", resolved[0].Message);
        }
        finally
        {
            if (File.Exists(statePath)) File.Delete(statePath);
        }
    }

    [Fact]
    public void MetricFindingsPassedThrough()
    {
        var statePath = GetTempStatePath();
        try
        {
            var state = new FindingState(statePath);
            var results = new List<MonitorResult>
            {
                new(MonitorType.RepoHealth, DateTime.UtcNow, 100, Severity.Ok,
                    new List<Finding>
                    {
                        new(Severity.Ok, "repo:Test", "metric", metricName: "veeam_test", metricValue: 42.0)
                    }, server: "server1")
            };

            var (filtered, resolved) = state.ProcessResults(results);

            Assert.Single(filtered[0].Findings);
            Assert.Equal("veeam_test", filtered[0].Findings[0].MetricName);
            Assert.Empty(resolved);
        }
        finally
        {
            if (File.Exists(statePath)) File.Delete(statePath);
        }
    }
}
