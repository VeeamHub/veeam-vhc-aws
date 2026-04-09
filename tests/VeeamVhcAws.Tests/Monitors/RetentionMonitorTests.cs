using Xunit;
using NSubstitute;
using VeeamVhcAws.Core.Clients;
using VeeamVhcAws.Core.Models;
using VeeamVhcAws.Core.Patterns;
using VeeamVhcAws.Infrastructure;
using VeeamVhcAws.Monitors;

namespace VeeamVhcAws.Tests.Monitors;

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

        var orphanFinding = result.Findings.First(f =>
            f.Message.Contains("orphan", StringComparison.OrdinalIgnoreCase) && f.Severity == Severity.Warning);

        Assert.Contains("Workloads:", orphanFinding.Message);
        Assert.Contains("vm-legacy", orphanFinding.Message);

        var workloads = Assert.IsType<List<string>>(orphanFinding.Details["workloads"]);
        Assert.Contains("vm-legacy", workloads);

        var orphanMetric = result.Findings.Where(f => f.MetricName == "veeam_retention_orphaned_backups").ToList();
        Assert.Single(orphanMetric);
        Assert.True(orphanMetric[0].MetricValue >= 1.0);
    }

    [Fact]
    public void TestOrphanDetectionMultipleWorkloads()
    {
        var now = DateTime.UtcNow;
        var rps = new List<Dictionary<string, object>>
        {
            new() { ["id"] = "rp-001", ["backupId"] = "backup-orphan", ["name"] = "vm-app-01",
                ["creationTime"] = now.AddDays(-1).ToString("O") },
            new() { ["id"] = "rp-002", ["backupId"] = "backup-orphan", ["name"] = "vm-db-01",
                ["creationTime"] = now.AddDays(-2).ToString("O") },
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

        var orphanFinding = result.Findings.First(f =>
            f.Message.Contains("orphan", StringComparison.OrdinalIgnoreCase) && f.Severity == Severity.Warning);

        Assert.Contains("vm-app-01", orphanFinding.Message);
        Assert.Contains("vm-db-01", orphanFinding.Message);
        Assert.Contains("Workloads:", orphanFinding.Message);

        var workloads = Assert.IsType<List<string>>(orphanFinding.Details["workloads"]);
        Assert.Equal(2, workloads.Count);
        Assert.Contains("vm-app-01", workloads);
        Assert.Contains("vm-db-01", workloads);
    }

    [Fact]
    public void TestOrphanDetectionVmNameFallback()
    {
        var now = DateTime.UtcNow;
        var rps = new List<Dictionary<string, object>>
        {
            new() { ["id"] = "rp-001", ["backupId"] = "backup-orphan", ["name"] = "",
                ["vmName"] = "legacy-vm-fallback",
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

        var orphanFinding = result.Findings.First(f =>
            f.Message.Contains("orphan", StringComparison.OrdinalIgnoreCase) && f.Severity == Severity.Warning);

        Assert.Contains("legacy-vm-fallback", orphanFinding.Message);
        Assert.Contains("Workloads:", orphanFinding.Message);

        var workloads = Assert.IsType<List<string>>(orphanFinding.Details["workloads"]);
        Assert.Contains("legacy-vm-fallback", workloads);
    }

    [Fact]
    public void TestOrphanDetectionNasWorkloadsCollapsed()
    {
        // NAS restore points come back with " Id: N" ordinal suffixes (e.g. "\\syn01\docker Id: 10").
        // They should collapse to a single workload entry per share path.
        var now = DateTime.UtcNow;
        var rps = Enumerable.Range(0, 94).Select(i => new Dictionary<string, object>
        {
            ["id"] = $"rp-{i:D3}",
            ["backupId"] = "backup-nas",
            ["name"] = $@"\\syn01\docker Id: {i}",
            ["creationTime"] = now.AddDays(-1).ToString("O"),
        }).ToList<Dictionary<string, object>>();

        var client = Substitute.For<IVbrClient>();
        client.GetJobs().Returns(new List<Dictionary<string, object>>
        {
            new() { ["id"] = "job-active", ["name"] = "Active Job",
                ["storage"] = new Dictionary<string, object> { ["retentionPolicy"] = new Dictionary<string, object> { ["type"] = "Days", ["quantity"] = 14 } } },
        });
        client.GetBackups().Returns(new List<Dictionary<string, object>>
        {
            new() { ["id"] = "backup-001", ["jobId"] = "job-active", ["name"] = "Active-Backup" },
            new() { ["id"] = "backup-nas", ["jobId"] = "job-gone", ["name"] = "PROD - NAS - Docker" },
        });
        client.GetRestorePoints(Arg.Any<int>(), Arg.Any<int>()).Returns(call =>
            call.ArgAt<int>(1) == 0 ? rps : new List<Dictionary<string, object>>());

        var ctx = new ServerContext("test-server", "vbr", VbrClient: client);
        var monitor = new RetentionMonitor(DefaultConfig());
        var result = monitor.Run(ctx, new PatternEngine(new List<ErrorPattern>()));

        var orphanFinding = result.Findings.First(f =>
            f.Message.Contains("PROD - NAS - Docker", StringComparison.OrdinalIgnoreCase) && f.Severity == Severity.Warning);

        var workloads = Assert.IsType<List<string>>(orphanFinding.Details["workloads"]);
        Assert.Single(workloads);
        Assert.Equal(@"\\syn01\docker", workloads[0], ignoreCase: true);
        Assert.Contains(@"\\syn01\docker", orphanFinding.Message);
        Assert.DoesNotContain("Id: 0", orphanFinding.Message);
    }

    private static List<Dictionary<string, object>> MakeRetentionSessions(
        string sessionType = "Retention", string result = "Failed", string message = "Cannot find full backup",
        string name = "PROD - K8s Cluster - k8s-cp1", int count = 1)
    {
        return Enumerable.Range(0, count).Select(i => new Dictionary<string, object>
        {
            ["id"] = $"session-{i}",
            ["sessionType"] = sessionType,
            ["name"] = name,
            ["result"] = new Dictionary<string, object>
            {
                ["result"] = result,
                ["message"] = message,
            },
            ["creationTime"] = DateTime.UtcNow.AddHours(-1).ToString("O"),
        }).ToList();
    }

    private static Dictionary<string, object> SessionConfig(Dictionary<string, object>? extra = null)
    {
        var thresholds = new Dictionary<string, object>
        {
            ["overage_multiplier"] = 1.5,
            ["max_age_multiplier"] = 1.5,
            ["orphan_detection"] = false,
        };
        var retention = new Dictionary<string, object>
        {
            ["thresholds"] = thresholds,
            ["session_lookback_hours"] = 48,
        };
        if (extra != null)
            foreach (var (k, v) in extra) retention[k] = v;

        return new Dictionary<string, object> { ["retention"] = retention };
    }

    [Fact]
    public void TestRetentionSessionFailureDetected()
    {
        var client = Substitute.For<IVbrClient>();
        client.GetJobs().Returns(new List<Dictionary<string, object>>());
        client.GetBackups().Returns(new List<Dictionary<string, object>>());
        client.GetRestorePoints(Arg.Any<int>(), Arg.Any<int>()).Returns(new List<Dictionary<string, object>>());
        client.GetSessions(Arg.Any<int>()).Returns(MakeRetentionSessions());

        var ctx = new ServerContext("test-server", "vbr", VbrClient: client);
        var monitor = new RetentionMonitor(SessionConfig());
        var result = monitor.Run(ctx, new PatternEngine(new List<ErrorPattern>()));

        Assert.Contains(result.Findings, f =>
            f.Message.Contains("Retention") && f.Message.Contains("Cannot find full backup") &&
            f.Severity == Severity.Critical);

        var metric = result.Findings.First(f => f.MetricName == "veeam_retention_session_failures");
        Assert.Equal(1.0, metric.MetricValue);
    }

    [Fact]
    public void TestRetentionSessionWarningDetected()
    {
        var client = Substitute.For<IVbrClient>();
        client.GetJobs().Returns(new List<Dictionary<string, object>>());
        client.GetBackups().Returns(new List<Dictionary<string, object>>());
        client.GetRestorePoints(Arg.Any<int>(), Arg.Any<int>()).Returns(new List<Dictionary<string, object>>());
        client.GetSessions(Arg.Any<int>()).Returns(
            MakeRetentionSessions(result: "Warning", message: "Minor retention issue"));

        var ctx = new ServerContext("test-server", "vbr", VbrClient: client);
        var monitor = new RetentionMonitor(SessionConfig());
        var result = monitor.Run(ctx, new PatternEngine(new List<ErrorPattern>()));

        Assert.Contains(result.Findings, f =>
            f.Message.Contains("Retention") && f.Severity == Severity.Warning);
    }

    [Fact]
    public void TestRetentionSessionExcludeJobMuted()
    {
        var client = Substitute.For<IVbrClient>();
        client.GetJobs().Returns(new List<Dictionary<string, object>>());
        client.GetBackups().Returns(new List<Dictionary<string, object>>());
        client.GetRestorePoints(Arg.Any<int>(), Arg.Any<int>()).Returns(new List<Dictionary<string, object>>());
        client.GetSessions(Arg.Any<int>()).Returns(
            MakeRetentionSessions(name: "PROD - Physical Laptop Backups - 192.168.20.2"));

        var cfg = SessionConfig(new Dictionary<string, object>
        {
            ["exclude_jobs"] = new List<object> { "Physical Laptop Backups" },
        });

        var ctx = new ServerContext("test-server", "vbr", VbrClient: client);
        var monitor = new RetentionMonitor(cfg);
        var result = monitor.Run(ctx, new PatternEngine(new List<ErrorPattern>()));

        Assert.DoesNotContain(result.Findings, f =>
            f.Message.Contains("Cannot find full backup"));

        var metric = result.Findings.First(f => f.MetricName == "veeam_retention_session_failures");
        Assert.Equal(0.0, metric.MetricValue);
    }

    [Fact]
    public void TestRetentionSessionExcludeErrorPatternMuted()
    {
        var client = Substitute.For<IVbrClient>();
        client.GetJobs().Returns(new List<Dictionary<string, object>>());
        client.GetBackups().Returns(new List<Dictionary<string, object>>());
        client.GetRestorePoints(Arg.Any<int>(), Arg.Any<int>()).Returns(new List<Dictionary<string, object>>());
        client.GetSessions(Arg.Any<int>()).Returns(
            MakeRetentionSessions(message: "Cannot find full backup"));

        var cfg = SessionConfig(new Dictionary<string, object>
        {
            ["exclude_session_errors"] = new List<object> { "Cannot find full backup" },
        });

        var ctx = new ServerContext("test-server", "vbr", VbrClient: client);
        var monitor = new RetentionMonitor(cfg);
        var result = monitor.Run(ctx, new PatternEngine(new List<ErrorPattern>()));

        Assert.DoesNotContain(result.Findings, f =>
            f.Message.Contains("Cannot find full backup"));

        var metric = result.Findings.First(f => f.MetricName == "veeam_retention_session_failures");
        Assert.Equal(0.0, metric.MetricValue);
    }

    [Fact]
    public void TestDefaultSessionLookbackIs24Hours()
    {
        var client = Substitute.For<IVbrClient>();
        client.GetJobs().Returns(new List<Dictionary<string, object>>());
        client.GetBackups().Returns(new List<Dictionary<string, object>>());
        client.GetRestorePoints(Arg.Any<int>(), Arg.Any<int>()).Returns(new List<Dictionary<string, object>>());
        client.GetSessions(Arg.Any<int>()).Returns(new List<Dictionary<string, object>>());

        var ctx = new ServerContext("test-server", "vbr", VbrClient: client);
        var monitor = new RetentionMonitor(DefaultConfig());
        monitor.Run(ctx, new PatternEngine(new List<ErrorPattern>()));

        client.Received(1).GetSessions(Arg.Is<int>(h => h == 24));
    }

    [Fact]
    public void TestRetentionSessionGroupsDuplicates()
    {
        var client = Substitute.For<IVbrClient>();
        client.GetJobs().Returns(new List<Dictionary<string, object>>());
        client.GetBackups().Returns(new List<Dictionary<string, object>>());
        client.GetRestorePoints(Arg.Any<int>(), Arg.Any<int>()).Returns(new List<Dictionary<string, object>>());
        client.GetSessions(Arg.Any<int>()).Returns(
            MakeRetentionSessions(count: 3));

        var ctx = new ServerContext("test-server", "vbr", VbrClient: client);
        var monitor = new RetentionMonitor(SessionConfig());
        var result = monitor.Run(ctx, new PatternEngine(new List<ErrorPattern>()));

        var sessionFindings = result.Findings
            .Where(f => f.Resource.StartsWith("session:") && f.Severity != Severity.Ok).ToList();
        Assert.Single(sessionFindings);
        Assert.Contains("3x", sessionFindings[0].Message);
    }
}
