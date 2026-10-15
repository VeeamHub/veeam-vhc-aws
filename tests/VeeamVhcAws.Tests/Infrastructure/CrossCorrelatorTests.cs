using VeeamVhcAws.Core.Models;
using VeeamVhcAws.Infrastructure;
using Xunit;

namespace VeeamVhcAws.Tests.Infrastructure;

public class CrossCorrelatorTests
{
    [Fact]
    public void WidespreadFailures_MatchAfterServerPrefix()
    {
        var results = new List<MonitorResult>
        {
            new(MonitorType.WorkerHealth, DateTime.UtcNow, 10, Severity.Warning,
                new List<Finding>
                {
                    new(Severity.Warning, "[prod] failed:policy-a", "timeout"),
                    new(Severity.Critical, "[prod] failed:policy-b", "timeout"),
                },
                server: "prod"),
        };

        var correlated = CrossCorrelator.Correlate(results);

        Assert.Contains(correlated, r =>
            r.Monitor == MonitorType.CrossCorrelation &&
            r.Findings.Any(f => f.Message.Contains("Widespread session failures", StringComparison.Ordinal)));
    }

    [Fact]
    public void Breadth_CountsDistinctPrefixedPolicies_AndStaysWarningWhenNoneIsCritical()
    {
        var results = new List<MonitorResult>
        {
            Worker("prod",
                new Finding(Severity.Warning, "[prod] failed:policy-a", "timeout"),
                new Finding(Severity.Warning, "[prod] failed:policy-a", "timeout again"),
                new Finding(Severity.Warning, "[prod] failed:policy-b", "timeout"),
                new Finding(Severity.Warning, "[prod] failed:policy-c", "timeout"),
                new Finding(Severity.Warning, "[prod] subnet:10.0.0.0/24", "not a policy")),
        };

        var breadth = Correlation(CrossCorrelator.Correlate(results), "prod", "M3-breadth");

        Assert.Equal(Severity.Warning, breadth.Severity);
        Assert.Equal("[prod] cross-correlation", breadth.Resource);
        Assert.Equal("Widespread session failures: 3 policies failing", breadth.Message);
        Assert.Equal(3, breadth.Details["count"]);
    }

    [Fact]
    public void Breadth_IsPerServer_OneFailedPolicyOnEachServerIsNotWidespread()
    {
        var results = new List<MonitorResult>
        {
            Worker("east", new Finding(Severity.Critical, "[east] failed:policy-a", "timeout")),
            Worker("west", new Finding(Severity.Critical, "[west] failed:policy-a", "timeout")),
        };

        var correlated = CrossCorrelator.Correlate(results);

        Assert.DoesNotContain(correlated, r => r.Monitor == MonitorType.CrossCorrelation);
    }

    [Fact]
    public void Breadth_IgnoresFailedResourcesFromOtherMonitors()
    {
        var results = new List<MonitorResult>
        {
            new(MonitorType.Retention, DateTime.UtcNow, 1, Severity.Ok,
                new List<Finding>
                {
                    new(Severity.Ok, "[prod] failed:job-a", "retention text"),
                    new(Severity.Ok, "[prod] failed:job-b", "retention text"),
                },
                server: "prod"),
        };

        var correlated = CrossCorrelator.Correlate(results);

        Assert.DoesNotContain(correlated, r => r.Monitor == MonitorType.CrossCorrelation);
    }

    [Fact]
    public void CredentialFailure_WithCriticalWorkerFinding_IsACriticalCascade()
    {
        var results = new List<MonitorResult>
        {
            new(MonitorType.RepoHealth, DateTime.UtcNow, 1, Severity.Warning,
                new List<Finding>
                {
                    new(Severity.Warning, "[prod] repo:s3-bucket", "Access key has expired for the repository"),
                },
                server: "prod"),
            Worker("prod", new Finding(Severity.Critical, "[prod] retention-session", "Retention session failed")),
        };

        var correlated = CrossCorrelator.Correlate(results);

        var result = Assert.Single(correlated, r => r.Monitor == MonitorType.CrossCorrelation);
        Assert.Equal("prod", result.Server);
        Assert.Equal(Severity.Critical, result.OverallSeverity);
        var cascade = Correlation(correlated, "prod", "M1+M3");
        Assert.Equal(Severity.Critical, cascade.Severity);
        Assert.Contains("Credential expiry cascade", cascade.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CredentialFailure_WithoutAWorkerFailure_IsNotCorrelated()
    {
        var results = new List<MonitorResult>
        {
            new(MonitorType.RepoHealth, DateTime.UtcNow, 1, Severity.Warning,
                new List<Finding> { new(Severity.Warning, "[prod] repo:s3-bucket", "Credential check failed") },
                server: "prod"),
            Worker("prod", new Finding(Severity.Warning, "[prod] failure-rate", "10% of sessions failed")),
        };

        var correlated = CrossCorrelator.Correlate(results);

        Assert.DoesNotContain(correlated, r => r.Monitor == MonitorType.CrossCorrelation);
    }

    [Fact]
    public void CredentialFailure_OnOneServer_DoesNotCorrelateWithAWorkerFailureOnAnother()
    {
        var results = new List<MonitorResult>
        {
            new(MonitorType.RepoHealth, DateTime.UtcNow, 1, Severity.Warning,
                new List<Finding> { new(Severity.Warning, "[vbr] repo:s3", "credential rejected") },
                server: "vbr"),
            Worker("vbaws", new Finding(Severity.Critical, "[vbaws] retention-session", "timeout")),
        };

        var correlated = CrossCorrelator.Correlate(results);

        Assert.DoesNotContain(correlated, r => r.Monitor == MonitorType.CrossCorrelation);
    }

    private static MonitorResult Worker(string server, params Finding[] findings) =>
        new(MonitorType.WorkerHealth, DateTime.UtcNow, 1, Severity.Warning, findings.ToList(), server: server);

    private static Finding Correlation(List<MonitorResult> results, string server, string correlation) =>
        Assert.Single(
            results
                .Where(r => r.Monitor == MonitorType.CrossCorrelation && r.Server == server)
                .SelectMany(r => r.Findings),
            f => Equals(f.Details.GetValueOrDefault("correlation"), correlation));
}
