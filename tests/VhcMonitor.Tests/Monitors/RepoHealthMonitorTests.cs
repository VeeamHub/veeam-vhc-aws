using Xunit;
using System.Text.Json;
using NSubstitute;
using VhcMonitor.Core.Clients;
using VhcMonitor.Core.Config;
using VhcMonitor.Core.Models;
using VhcMonitor.Core.Patterns;
using VhcMonitor.Infrastructure;
using VhcMonitor.Monitors;

namespace VhcMonitor.Tests.Monitors;

public class RepoHealthMonitorTests
{
    private static readonly string FixturesPath = Path.Combine(
        AppContext.BaseDirectory, "Fixtures");

    private static List<Dictionary<string, object>> LoadFixture(string name)
    {
        var json = File.ReadAllText(Path.Combine(FixturesPath, name));
        var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateArray()
            .Select(e => VbrClient.JsonElementToDict(e))
            .ToList();
    }

    private static Dictionary<string, object> DefaultConfig(Dictionary<string, object>? overrides = null)
    {
        var cfg = new Dictionary<string, object>
        {
            ["repo_health"] = new Dictionary<string, object>
            {
                ["thresholds"] = new Dictionary<string, object>
                {
                    ["free_space_warning_pct"] = 15,
                    ["free_space_critical_pct"] = 5,
                },
                ["check_external_maintenance"] = true,
                ["external_maintenance_lookback_hours"] = 48,
            }
        };
        if (overrides != null)
        {
            var rh = (Dictionary<string, object>)cfg["repo_health"];
            foreach (var (k, v) in overrides) rh[k] = v;
        }
        return cfg;
    }

    private static PatternEngine CredentialEngine() => new(new List<ErrorPattern>
    {
        new(@"(?i)access\s*key.*(?:invalid|expired|does not exist)", Severity.Critical, "AWS credential failure", "credential"),
        new(@"(?i)s3.*(?:timeout|connection refused|503)", Severity.Critical, "S3 connectivity failure", "s3"),
        new(@"(?i)authentication failed|unauthorized|401", Severity.Critical, "Auth failure", "auth"),
    });

    [Fact]
    public void TestHealthyRepos()
    {
        var client = Substitute.For<IVbrClient>();
        client.GetRepositoryStates().Returns(new List<Dictionary<string, object>>
        {
            new() { ["name"] = "Healthy-Repo", ["type"] = "WinLocal", ["capacityGB"] = (long)1000, ["freeGB"] = (long)500 }
        });
        client.GetSessions(Arg.Any<int>()).Returns(new List<Dictionary<string, object>>());
        client.GetScaleoutRepositories().Returns(new List<Dictionary<string, object>>());

        var ctx = new ServerContext("test-server", "vbr", VbrClient: client);
        var monitor = new RepoHealthMonitor(DefaultConfig(new() { ["check_external_maintenance"] = false }));
        var result = monitor.Run(ctx, new PatternEngine(new List<ErrorPattern>()));

        Assert.Equal(MonitorType.RepoHealth, result.Monitor);
        Assert.Equal(Severity.Ok, result.OverallSeverity);
        var statusFindings = result.Findings.Where(f => f.MetricName == null).ToList();
        Assert.All(statusFindings, f => Assert.Equal(Severity.Ok, f.Severity));
    }

    [Fact]
    public void TestLowSpaceWarning()
    {
        var client = Substitute.For<IVbrClient>();
        client.GetRepositoryStates().Returns(new List<Dictionary<string, object>>
        {
            new() { ["name"] = "Low-Repo", ["type"] = "WinLocal", ["capacityGB"] = (long)500, ["freeGB"] = (long)50 }
        });
        client.GetSessions(Arg.Any<int>()).Returns(new List<Dictionary<string, object>>());
        client.GetScaleoutRepositories().Returns(new List<Dictionary<string, object>>());

        var ctx = new ServerContext("test-server", "vbr", VbrClient: client);
        var monitor = new RepoHealthMonitor(DefaultConfig(new() { ["check_external_maintenance"] = false }));
        var result = monitor.Run(ctx, new PatternEngine(new List<ErrorPattern>()));

        Assert.Equal(Severity.Warning, result.OverallSeverity);
        Assert.Contains(result.Findings, f =>
            f.Severity == Severity.Warning && f.Message.Contains("low on space", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TestLowSpaceCritical()
    {
        var client = Substitute.For<IVbrClient>();
        client.GetRepositoryStates().Returns(new List<Dictionary<string, object>>
        {
            new() { ["name"] = "Critical-Repo", ["type"] = "WinLocal", ["capacityGB"] = (long)200, ["freeGB"] = (long)5 }
        });
        client.GetSessions(Arg.Any<int>()).Returns(new List<Dictionary<string, object>>());
        client.GetScaleoutRepositories().Returns(new List<Dictionary<string, object>>());

        var ctx = new ServerContext("test-server", "vbr", VbrClient: client);
        var monitor = new RepoHealthMonitor(DefaultConfig(new() { ["check_external_maintenance"] = false }));
        var result = monitor.Run(ctx, new PatternEngine(new List<ErrorPattern>()));

        Assert.Equal(Severity.Critical, result.OverallSeverity);
        Assert.Contains(result.Findings, f =>
            f.Severity == Severity.Critical && f.Message.Contains("critically low", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TestUnreachableRepo()
    {
        var client = Substitute.For<IVbrClient>();
        client.GetRepositoryStates().Returns(new List<Dictionary<string, object>>
        {
            new() { ["name"] = "Dead-Repo", ["type"] = "WinLocal", ["capacityGB"] = (long)0, ["freeGB"] = (long)0 }
        });
        client.GetSessions(Arg.Any<int>()).Returns(new List<Dictionary<string, object>>());
        client.GetScaleoutRepositories().Returns(new List<Dictionary<string, object>>());

        var ctx = new ServerContext("test-server", "vbr", VbrClient: client);
        var monitor = new RepoHealthMonitor(DefaultConfig(new() { ["check_external_maintenance"] = false }));
        var result = monitor.Run(ctx, new PatternEngine(new List<ErrorPattern>()));

        Assert.Equal(Severity.Error, result.OverallSeverity);
        Assert.Contains(result.Findings, f =>
            f.Severity == Severity.Error && f.Message.Contains("unreachable", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TestExpiredCredentialDetection()
    {
        var sessions = LoadFixture("sessions_external_maintenance.json");
        var client = Substitute.For<IVbrClient>();
        client.GetRepositoryStates().Returns(new List<Dictionary<string, object>>
        {
            new() { ["name"] = "S3-Repo", ["type"] = "ExternalS3", ["capacityGB"] = (long)5000, ["freeGB"] = (long)4000 }
        });
        client.GetSessions(Arg.Any<int>()).Returns(sessions);
        client.GetScaleoutRepositories().Returns(new List<Dictionary<string, object>>());

        var ctx = new ServerContext("test-server", "vbr", VbrClient: client);
        var engine = CredentialEngine();
        var monitor = new RepoHealthMonitor(DefaultConfig());
        var result = monitor.Run(ctx, engine);

        Assert.Equal(Severity.Critical, result.OverallSeverity);
        Assert.Contains(result.Findings, f =>
            f.Severity == Severity.Critical && f.Message.Contains("credential", StringComparison.OrdinalIgnoreCase));

        var credMetrics = result.Findings.Where(f => f.MetricName == "veeam_repo_credential_expired").ToList();
        Assert.Single(credMetrics);
        Assert.True(credMetrics[0].MetricValue >= 1.0);
    }

    [Fact]
    public void TestNoExternalMaintenanceSessions()
    {
        var client = Substitute.For<IVbrClient>();
        client.GetRepositoryStates().Returns(new List<Dictionary<string, object>>
        {
            new() { ["name"] = "S3-Repo", ["type"] = "ExternalS3", ["capacityGB"] = (long)5000, ["freeGB"] = (long)4000 }
        });
        client.GetSessions(Arg.Any<int>()).Returns(new List<Dictionary<string, object>>());
        client.GetScaleoutRepositories().Returns(new List<Dictionary<string, object>>());

        var ctx = new ServerContext("test-server", "vbr", VbrClient: client);
        var monitor = new RepoHealthMonitor(DefaultConfig());
        var result = monitor.Run(ctx, new PatternEngine(new List<ErrorPattern>()));

        Assert.Contains(result.Findings, f =>
            f.Message.Contains("no sessions found", StringComparison.OrdinalIgnoreCase));
    }
}
