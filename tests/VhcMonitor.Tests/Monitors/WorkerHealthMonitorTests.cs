using Xunit;
using System.Text.Json;
using NSubstitute;
using VhcMonitor.Core.Clients;
using VhcMonitor.Core.Models;
using VhcMonitor.Core.Patterns;
using VhcMonitor.Infrastructure;
using VhcMonitor.Monitors;

namespace VhcMonitor.Tests.Monitors;

public class WorkerHealthMonitorTests
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
        var thresholds = new Dictionary<string, object>
        {
            ["session_failure_rate_warning"] = 0.1,
            ["session_failure_rate_critical"] = 0.3,
            ["retention_session_max_failures"] = 1,
            ["zero_deleted_items_warning"] = true,
            ["recurring_failure_threshold"] = 3,
        };
        if (overrides != null)
            foreach (var (k, v) in overrides) thresholds[k] = v;

        return new Dictionary<string, object>
        {
            ["worker_health"] = new Dictionary<string, object>
            {
                ["lookback_hours"] = 24,
                ["thresholds"] = thresholds,
            }
        };
    }

    private static PatternEngine SubnetEngine() => new(new List<ErrorPattern>
    {
        new(
            pattern: @"(?i)(?:cannot allocate|not enough free addresses|insufficient\s*free\s*addresses).*(?:subnet[- ]?(?P<subnet_id>subnet-[a-z0-9]+))",
            severity: Severity.Critical,
            message: "Subnet IP exhaustion",
            category: "network",
            extractFields: new List<string> { "subnet_id" },
            logHint: "/var/log/veeam/worker.log",
            remediation: "Add a secondary subnet or increase CIDR range"
        ),
        new(
            pattern: @"(?i)access\s*key.*(?:invalid|expired|does not exist)",
            severity: Severity.Critical,
            message: "Credential failure",
            category: "credential"
        ),
    });

    [Fact]
    public void TestHealthySessions()
    {
        var sessions = new List<Dictionary<string, object>>
        {
            new() { ["type"] = "BackupSession", ["status"] = "Success", ["result"] = new Dictionary<string, object> { ["message"] = "" }, ["name"] = "b1" },
            new() { ["type"] = "BackupSession", ["status"] = "Success", ["result"] = new Dictionary<string, object> { ["message"] = "" }, ["name"] = "b2" },
            new() { ["type"] = "RetentionSession", ["status"] = "Success", ["deletedItems"] = (long)3, ["result"] = new Dictionary<string, object> { ["message"] = "" }, ["name"] = "r1" },
        };

        var client = Substitute.For<IVbawsClient>();
        client.GetSessions(Arg.Any<DateTime>(), Arg.Any<DateTime>()).Returns(sessions);

        var ctx = new ServerContext("test-server", "vbaws", VbawsClient: client);
        var monitor = new WorkerHealthMonitor(DefaultConfig());
        var result = monitor.Run(ctx, new PatternEngine(new List<ErrorPattern>()));

        Assert.Equal(MonitorType.WorkerHealth, result.Monitor);
        var rateFindings = result.Findings
            .Where(f => f.Message.Contains("failure rate", StringComparison.OrdinalIgnoreCase) && f.Severity != Severity.Ok)
            .ToList();
        Assert.Empty(rateFindings);
    }

    [Fact]
    public void TestHighFailureRate()
    {
        var sessions = new List<Dictionary<string, object>>
        {
            new() { ["type"] = "BackupSession", ["status"] = "Failed", ["result"] = new Dictionary<string, object> { ["message"] = "timeout" }, ["name"] = "b1" },
            new() { ["type"] = "BackupSession", ["status"] = "Failed", ["result"] = new Dictionary<string, object> { ["message"] = "timeout" }, ["name"] = "b2" },
            new() { ["type"] = "BackupSession", ["status"] = "Success", ["result"] = new Dictionary<string, object> { ["message"] = "" }, ["name"] = "b3" },
        };

        var client = Substitute.For<IVbawsClient>();
        client.GetSessions(Arg.Any<DateTime>(), Arg.Any<DateTime>()).Returns(sessions);

        var ctx = new ServerContext("test-server", "vbaws", VbawsClient: client);
        var monitor = new WorkerHealthMonitor(DefaultConfig());
        var result = monitor.Run(ctx, new PatternEngine(new List<ErrorPattern>()));

        Assert.True(result.OverallSeverity is Severity.Warning or Severity.Critical);
        Assert.Contains(result.Findings, f =>
            f.Message.Contains("failure rate", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TestSubnetExhaustionDetection()
    {
        var sessions = LoadFixture("vbaws_sessions.json");
        // Patch fixture dates to be within lookback window
        var recent = DateTime.UtcNow.AddHours(-1).ToString("O");
        foreach (var s in sessions)
            s["creationTime"] = recent;

        var client = Substitute.For<IVbawsClient>();
        client.GetSessions(Arg.Any<DateTime>(), Arg.Any<DateTime>()).Returns(sessions);

        var ctx = new ServerContext("test-server", "vbaws", VbawsClient: client);
        var engine = SubnetEngine();
        var monitor = new WorkerHealthMonitor(DefaultConfig());
        var result = monitor.Run(ctx, engine);

        Assert.Contains(result.Findings, f =>
            f.Message.Contains("subnet", StringComparison.OrdinalIgnoreCase) &&
            f.Message.Contains("exhaustion", StringComparison.OrdinalIgnoreCase));

        var subnetMetrics = result.Findings.Where(f => f.MetricName == "veeam_vbaws_subnet_exhaustion").ToList();
        Assert.Single(subnetMetrics);
        Assert.True(subnetMetrics[0].MetricValue >= 1.0);
    }

    [Fact]
    public void TestZeroDeletedItemsWarning()
    {
        var sessions = new List<Dictionary<string, object>>
        {
            new() { ["type"] = "RetentionSession", ["status"] = "Success", ["deletedItems"] = (long)0,
                ["result"] = new Dictionary<string, object> { ["message"] = "" }, ["name"] = "r1" },
        };

        var client = Substitute.For<IVbawsClient>();
        client.GetSessions(Arg.Any<DateTime>(), Arg.Any<DateTime>()).Returns(sessions);

        var ctx = new ServerContext("test-server", "vbaws", VbawsClient: client);
        var monitor = new WorkerHealthMonitor(DefaultConfig());
        var result = monitor.Run(ctx, new PatternEngine(new List<ErrorPattern>()));

        Assert.Contains(result.Findings, f =>
            f.Message.Contains("deleted nothing", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TestNoRetentionSessions()
    {
        var sessions = new List<Dictionary<string, object>>
        {
            new() { ["type"] = "BackupSession", ["status"] = "Success",
                ["result"] = new Dictionary<string, object> { ["message"] = "" }, ["name"] = "b1" },
        };

        var client = Substitute.For<IVbawsClient>();
        client.GetSessions(Arg.Any<DateTime>(), Arg.Any<DateTime>()).Returns(sessions);

        var ctx = new ServerContext("test-server", "vbaws", VbawsClient: client);
        var monitor = new WorkerHealthMonitor(DefaultConfig());
        var result = monitor.Run(ctx, new PatternEngine(new List<ErrorPattern>()));

        Assert.Contains(result.Findings, f =>
            f.Message.Contains("no retention sessions", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TestRecurringFailurePattern()
    {
        var sessions = Enumerable.Range(0, 4).Select(i => new Dictionary<string, object>
        {
            ["type"] = "BackupSession", ["status"] = "Failed",
            ["result"] = new Dictionary<string, object> { ["message"] = "The AWS Access Key Id you provided does not exist" },
            ["name"] = $"b{i}"
        }).ToList();
        sessions.Add(new Dictionary<string, object>
        {
            ["type"] = "RetentionSession", ["status"] = "Success", ["deletedItems"] = (long)1,
            ["result"] = new Dictionary<string, object> { ["message"] = "" }, ["name"] = "r1"
        });

        var client = Substitute.For<IVbawsClient>();
        client.GetSessions(Arg.Any<DateTime>(), Arg.Any<DateTime>()).Returns(sessions);

        var engine = SubnetEngine();
        var ctx = new ServerContext("test-server", "vbaws", VbawsClient: client);
        var monitor = new WorkerHealthMonitor(DefaultConfig());
        var result = monitor.Run(ctx, engine);

        Assert.Contains(result.Findings, f =>
            f.Message.Contains("recurring", StringComparison.OrdinalIgnoreCase) &&
            f.Message.Contains("credential", StringComparison.OrdinalIgnoreCase));
    }
}
