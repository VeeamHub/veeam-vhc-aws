using Xunit;
using NSubstitute;
using VhcMonitor.Core.Clients;
using VhcMonitor.Core.Models;
using VhcMonitor.Core.Patterns;
using VhcMonitor.Infrastructure;
using VhcMonitor.Monitors;

namespace VhcMonitor.Tests.Monitors;

public class RetentionMonitorTests
{
    private static Dictionary<string, object> DefaultConfig(Dictionary<string, object>? overrides = null)
    {
        var thresholds = new Dictionary<string, object>
        {
            ["overage_multiplier"] = 1.5,
            ["max_age_multiplier"] = 1.5,
            ["orphan_detection"] = true,
        };
        if (overrides != null)
            foreach (var (k, v) in overrides) thresholds[k] = v;

        return new Dictionary<string, object>
        {
            ["retention"] = new Dictionary<string, object>
            {
                ["thresholds"] = thresholds,
            }
        };
    }

    [Fact]
    public void TestNormalRetention()
    {
        var now = DateTime.UtcNow;
        var rps = Enumerable.Range(0, 7).Select(i => new Dictionary<string, object>
        {
            ["id"] = $"rp-{i}", ["backupId"] = "backup-001", ["name"] = "vm-web-01",
            ["creationTime"] = now.AddDays(-i).ToString("O")
        }).ToList();

        var client = Substitute.For<IVbrClient>();
        client.GetJobs().Returns(new List<Dictionary<string, object>>
        {
            new() { ["id"] = "job-001", ["name"] = "Daily-Backup",
                ["storage"] = new Dictionary<string, object> {
                    ["retentionPolicy"] = new Dictionary<string, object> { ["type"] = "cycles", ["quantity"] = (long)7 },
                    ["gfsPolicy"] = new Dictionary<string, object> { ["isEnabled"] = false }
                } }
        });
        client.GetBackups().Returns(new List<Dictionary<string, object>>
        {
            new() { ["id"] = "backup-001", ["jobId"] = "job-001", ["name"] = "Daily-Backup" }
        });
        client.GetRestorePoints(Arg.Any<int>(), Arg.Any<int>()).Returns(call =>
        {
            var offset = call.ArgAt<int>(1);
            return offset == 0 ? rps : new List<Dictionary<string, object>>();
        });

        var ctx = new ServerContext("test-server", "vbr", VbrClient: client);
        var monitor = new RetentionMonitor(DefaultConfig(new() { ["orphan_detection"] = false }));
        var result = monitor.Run(ctx, new PatternEngine(new List<ErrorPattern>()));

        Assert.Equal(MonitorType.Retention, result.Monitor);
        var violationFindings = result.Findings
            .Where(f => f.Severity is Severity.Warning or Severity.Critical).ToList();
        Assert.Empty(violationFindings);
    }

    [Fact]
    public void TestCyclesOverage()
    {
        var now = DateTime.UtcNow;
        var rps = Enumerable.Range(0, 11).Select(i => new Dictionary<string, object>
        {
            ["id"] = $"rp-{i}", ["backupId"] = "backup-001", ["name"] = "vm-web-01",
            ["creationTime"] = now.AddDays(-i).ToString("O")
        }).ToList();

        var client = Substitute.For<IVbrClient>();
        client.GetJobs().Returns(new List<Dictionary<string, object>>
        {
            new() { ["id"] = "job-001", ["name"] = "Daily-Backup",
                ["storage"] = new Dictionary<string, object> {
                    ["retentionPolicy"] = new Dictionary<string, object> { ["type"] = "cycles", ["quantity"] = (long)7 },
                    ["gfsPolicy"] = new Dictionary<string, object> { ["isEnabled"] = false }
                } }
        });
        client.GetBackups().Returns(new List<Dictionary<string, object>>
        {
            new() { ["id"] = "backup-001", ["jobId"] = "job-001", ["name"] = "Daily-Backup" }
        });
        client.GetRestorePoints(Arg.Any<int>(), Arg.Any<int>()).Returns(call =>
        {
            var offset = call.ArgAt<int>(1);
            return offset == 0 ? rps : new List<Dictionary<string, object>>();
        });

        var ctx = new ServerContext("test-server", "vbr", VbrClient: client);
        var monitor = new RetentionMonitor(DefaultConfig(new() { ["orphan_detection"] = false }));
        var result = monitor.Run(ctx, new PatternEngine(new List<ErrorPattern>()));

        Assert.True(result.OverallSeverity is Severity.Warning or Severity.Critical);
        Assert.Contains(result.Findings, f =>
            f.Message.Contains("overage", StringComparison.OrdinalIgnoreCase) &&
            f.Severity is Severity.Warning or Severity.Critical);
    }

    [Fact]
    public void TestDaysOverage()
    {
        var now = DateTime.UtcNow;
        // 8 restore points with quantity=5, threshold = 5 * 1.5 = 7 → 8 > 7 triggers warning
        var rps = Enumerable.Range(0, 8).Select(i => new Dictionary<string, object>
        {
            ["id"] = $"rp-{i}", ["backupId"] = "backup-002", ["name"] = "vm-db-01",
            ["creationTime"] = now.AddDays(-i).ToString("O")
        }).ToList();

        var client = Substitute.For<IVbrClient>();
        client.GetJobs().Returns(new List<Dictionary<string, object>>
        {
            new() { ["id"] = "job-002", ["name"] = "Weekly-Archive",
                ["storage"] = new Dictionary<string, object> {
                    ["retentionPolicy"] = new Dictionary<string, object> { ["type"] = "days", ["quantity"] = (long)5 },
                    ["gfsPolicy"] = new Dictionary<string, object> { ["isEnabled"] = false }
                } }
        });
        client.GetBackups().Returns(new List<Dictionary<string, object>>
        {
            new() { ["id"] = "backup-002", ["jobId"] = "job-002", ["name"] = "Weekly-Archive" }
        });
        client.GetRestorePoints(Arg.Any<int>(), Arg.Any<int>()).Returns(call =>
        {
            var offset = call.ArgAt<int>(1);
            return offset == 0 ? rps : new List<Dictionary<string, object>>();
        });

        var ctx = new ServerContext("test-server", "vbr", VbrClient: client);
        var monitor = new RetentionMonitor(DefaultConfig(new() { ["orphan_detection"] = false }));
        var result = monitor.Run(ctx, new PatternEngine(new List<ErrorPattern>()));

        Assert.Equal(Severity.Warning, result.OverallSeverity);
        Assert.Contains(result.Findings, f =>
            f.Message.Contains("overage", StringComparison.OrdinalIgnoreCase) &&
            f.Severity is Severity.Warning or Severity.Critical);
    }

    [Fact]
    public void TestOrphanDetection()
    {
        var now = DateTime.UtcNow;
        var rps = new List<Dictionary<string, object>>
        {
            new() { ["id"] = "rp-001", ["backupId"] = "backup-orphan", ["name"] = "vm-legacy",
                ["creationTime"] = now.AddDays(-1).ToString("O") },
        };

        var client = Substitute.For<IVbrClient>();
        client.GetJobs().Returns(new List<Dictionary<string, object>>
        {
            new() { ["id"] = "job-001", ["name"] = "Active-Job",
                ["storage"] = new Dictionary<string, object> {
                    ["retentionPolicy"] = new Dictionary<string, object> { ["type"] = "cycles", ["quantity"] = (long)7 },
                    ["gfsPolicy"] = new Dictionary<string, object> { ["isEnabled"] = false }
                } }
        });
        client.GetBackups().Returns(new List<Dictionary<string, object>>
        {
            new() { ["id"] = "backup-001", ["jobId"] = "job-001", ["name"] = "Active-Backup" },
            new() { ["id"] = "backup-orphan", ["jobId"] = "job-gone", ["name"] = "Old-Orphan-Backup" },
        });
        client.GetRestorePoints(Arg.Any<int>(), Arg.Any<int>()).Returns(call =>
        {
            var offset = call.ArgAt<int>(1);
            return offset == 0 ? rps : new List<Dictionary<string, object>>();
        });

        var ctx = new ServerContext("test-server", "vbr", VbrClient: client);
        var monitor = new RetentionMonitor(DefaultConfig());
        var result = monitor.Run(ctx, new PatternEngine(new List<ErrorPattern>()));

        Assert.Contains(result.Findings, f =>
            f.Message.Contains("orphan", StringComparison.OrdinalIgnoreCase) && f.Severity == Severity.Warning);

        var orphanMetric = result.Findings.Where(f => f.MetricName == "veeam_retention_orphaned_backups").ToList();
        Assert.Single(orphanMetric);
        Assert.True(orphanMetric[0].MetricValue >= 1.0);
    }
}
