using System.Text.Json;
using VeeamVhcAws.Commands;
using VeeamVhcAws.Core.Models;
using VeeamVhcAws.Core.State;
using VeeamVhcAws.Web;
using VeeamVhcAws.Web.Services;
using Xunit;

namespace VeeamVhcAws.Tests.Core;

/// <summary>
/// A suppressed finding must not drive the overall severity or the exit code. Both are
/// computed from the findings that remain after suppression.
/// </summary>
public class SuppressedSeverityTests : IDisposable
{
    private readonly string _dir;
    private readonly string _configPath;
    private readonly string _statePath;

    public SuppressedSeverityTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "vhc-suppressed-sev-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _configPath = Path.Combine(_dir, "veeam-vhc-aws.yaml");
        _statePath = Path.Combine(_dir, "veeam-vhc-aws-state.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private static List<MonitorResult> Run(params Finding[] findings)
    {
        var worst = findings.Aggregate(Severity.Ok, (acc, f) => f.Severity.Rank() > acc.Rank() ? f.Severity : acc);
        return new List<MonitorResult>
        {
            new(MonitorType.RepoHealth, DateTime.UtcNow, 10, worst, findings.ToList(), server: "s1"),
        };
    }

    [Fact]
    public void ConfigSuppressedCritical_DoesNotSetSeverityOrExitCode()
    {
        var state = new FindingState(_statePath, new List<string> { "immutability disabled" });
        var results = Run(
            new Finding(Severity.Critical, "repo:a", "Repository immutability disabled"),
            new Finding(Severity.Warning, "repo:b", "Repository is low on space"));

        var (filtered, _) = state.ProcessResults(results);

        Assert.Equal(Severity.Warning, filtered[0].OverallSeverity);
        Assert.Equal(1, CommandHelpers.WorstExitCode(results));
    }

    [Fact]
    public void CapturesSuppressedCritical_ExitsOk_WhenNothingElseRemains()
    {
        var first = Run(new Finding(Severity.Critical, "repo:a", "Repository immutability disabled"));
        new FindingState(_statePath).ProcessResults(first);
        Assert.Equal(2, CommandHelpers.WorstExitCode(first));

        using (var doc = JsonDocument.Parse(File.ReadAllText(_statePath)))
        {
            var key = doc.RootElement.GetProperty("captured_errors").EnumerateObject().Single().Name;
            Assert.True(new StateService(new WebHostOptions(_configPath, "127.0.0.1", 0)).Suppress(key));
        }

        var second = Run(new Finding(Severity.Critical, "repo:a", "Repository immutability disabled"));
        var (filtered, _) = new FindingState(_statePath).ProcessResults(second);

        Assert.Equal(Severity.Ok, filtered[0].OverallSeverity);
        Assert.Equal(0, CommandHelpers.WorstExitCode(second));
    }

    [Fact]
    public void SuppressedFinding_WithMonitorErrors_StaysAtWarning()
    {
        var state = new FindingState(_statePath, new List<string> { "immutability disabled" });
        var results = Run(new Finding(Severity.Critical, "repo:a", "Repository immutability disabled"));
        results[0].Errors.Add("GET /api/v1/backupInfrastructure/repositories/states timed out");

        var (filtered, _) = state.ProcessResults(results);

        Assert.Equal(Severity.Warning, filtered[0].OverallSeverity);
        Assert.Equal(1, CommandHelpers.WorstExitCode(results));
    }

    [Fact]
    public void UnsuppressedRun_KeepsMonitorSeverity()
    {
        var state = new FindingState(_statePath);
        var results = Run(new Finding(Severity.Critical, "repo:a", "Repository immutability disabled"));

        var (filtered, _) = state.ProcessResults(results);

        Assert.Equal(Severity.Critical, filtered[0].OverallSeverity);
        Assert.Equal(2, CommandHelpers.WorstExitCode(results));
    }
}
