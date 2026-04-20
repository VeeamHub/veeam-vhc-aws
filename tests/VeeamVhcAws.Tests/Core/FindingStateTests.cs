using System.Text.Json;
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

    [Fact]
    public void NonOkFindingsCapturedInState()
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
                        new(Severity.Warning, "repo:Test", "Low space on repository")
                    }, server: "server1")
            };

            state.ProcessResults(results);

            var json = File.ReadAllText(statePath);
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            Assert.True(root.TryGetProperty("captured_errors", out var captured));
            Assert.Single(captured.EnumerateObject());

            var entry = captured.EnumerateObject().First().Value;
            Assert.Equal("warning", entry.GetProperty("severity").GetString());
            Assert.Equal("repo:Test", entry.GetProperty("resource").GetString());
            Assert.Equal("server1", entry.GetProperty("server").GetString());
            Assert.Equal("repo_health", entry.GetProperty("monitor").GetString());
            Assert.False(entry.GetProperty("suppressed").GetBoolean());
            Assert.Equal(1.0, entry.GetProperty("count").GetDouble());
        }
        finally
        {
            if (File.Exists(statePath)) File.Delete(statePath);
        }
    }

    [Fact]
    public void CapturedErrorCountIncrementsOnRepeat()
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

            var state2 = new FindingState(statePath);
            state2.ProcessResults(results);

            var json = File.ReadAllText(statePath);
            var doc = JsonDocument.Parse(json);
            var captured = doc.RootElement.GetProperty("captured_errors");
            var entry = captured.EnumerateObject().First().Value;
            Assert.Equal(2.0, entry.GetProperty("count").GetDouble());
        }
        finally
        {
            if (File.Exists(statePath)) File.Delete(statePath);
        }
    }

    [Fact]
    public void ConfigSuppressionsSkipFinding()
    {
        var statePath = GetTempStatePath();
        try
        {
            var suppressions = new List<string> { "Low space" };
            var state = new FindingState(statePath, suppressions);
            var results = new List<MonitorResult>
            {
                new(MonitorType.RepoHealth, DateTime.UtcNow, 100, Severity.Warning,
                    new List<Finding>
                    {
                        new(Severity.Warning, "repo:Test", "Low space on repository")
                    }, server: "server1")
            };

            var (filtered, resolved) = state.ProcessResults(results);

            // Finding should be filtered out (not emitted)
            Assert.Empty(filtered[0].Findings);

            // But should be captured in state as suppressed
            var json = File.ReadAllText(statePath);
            var doc = JsonDocument.Parse(json);
            var captured = doc.RootElement.GetProperty("captured_errors");
            var entry = captured.EnumerateObject().First().Value;
            Assert.True(entry.GetProperty("suppressed").GetBoolean());
        }
        finally
        {
            if (File.Exists(statePath)) File.Delete(statePath);
        }
    }

    [Fact]
    public void ConfigSuppressionsAreCaseInsensitive()
    {
        var statePath = GetTempStatePath();
        try
        {
            var suppressions = new List<string> { "LOW SPACE" };
            var state = new FindingState(statePath, suppressions);
            var results = new List<MonitorResult>
            {
                new(MonitorType.RepoHealth, DateTime.UtcNow, 100, Severity.Warning,
                    new List<Finding>
                    {
                        new(Severity.Warning, "repo:Test", "Low space on repository")
                    }, server: "server1")
            };

            var (filtered, _) = state.ProcessResults(results);
            Assert.Empty(filtered[0].Findings);
        }
        finally
        {
            if (File.Exists(statePath)) File.Delete(statePath);
        }
    }

    [Fact]
    public void StateSuppressionsSkipFinding()
    {
        var statePath = GetTempStatePath();
        try
        {
            // First pass — capture the finding
            var state = new FindingState(statePath);
            var finding = new Finding(Severity.Warning, "repo:Test", "Low space");
            var results = new List<MonitorResult>
            {
                new(MonitorType.RepoHealth, DateTime.UtcNow, 100, Severity.Warning,
                    new List<Finding> { finding }, server: "server1")
            };
            state.ProcessResults(results);

            // Get the key from captured_errors
            var json = File.ReadAllText(statePath);
            var doc = JsonDocument.Parse(json);
            var capturedKey = doc.RootElement.GetProperty("captured_errors").EnumerateObject().First().Name;

            // Write a suppression to state file
            var stateDict = JsonSerializer.Deserialize<Dictionary<string, object>>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
            var suppressionsDict = new Dictionary<string, object>
            {
                [capturedKey] = new Dictionary<string, object>
                {
                    ["suppressed_at"] = DateTime.UtcNow.ToString("O")
                }
            };
            stateDict["suppressions"] = suppressionsDict;
            File.WriteAllText(statePath, JsonSerializer.Serialize(stateDict, new JsonSerializerOptions { WriteIndented = true }));

            // Second pass — finding should be suppressed
            var state2 = new FindingState(statePath);
            var (filtered, _) = state2.ProcessResults(results);
            Assert.Empty(filtered[0].Findings);

            // Verify it's marked suppressed in captured_errors
            json = File.ReadAllText(statePath);
            doc = JsonDocument.Parse(json);
            var entry = doc.RootElement.GetProperty("captured_errors").GetProperty(capturedKey);
            Assert.True(entry.GetProperty("suppressed").GetBoolean());
        }
        finally
        {
            if (File.Exists(statePath)) File.Delete(statePath);
        }
    }

    [Fact]
    public void CapturedErrorTextTruncatedTo120Chars()
    {
        var statePath = GetTempStatePath();
        try
        {
            var longMessage = new string('x', 200);
            var state = new FindingState(statePath);
            var results = new List<MonitorResult>
            {
                new(MonitorType.RepoHealth, DateTime.UtcNow, 100, Severity.Warning,
                    new List<Finding>
                    {
                        new(Severity.Warning, "repo:Test", longMessage)
                    }, server: "server1")
            };
            state.ProcessResults(results);

            var json = File.ReadAllText(statePath);
            var doc = JsonDocument.Parse(json);
            var entry = doc.RootElement.GetProperty("captured_errors").EnumerateObject().First().Value;
            var text = entry.GetProperty("text").GetString()!;
            Assert.Equal(120, text.Length);
        }
        finally
        {
            if (File.Exists(statePath)) File.Delete(statePath);
        }
    }

    [Fact]
    public void MetricsAndOkNotCaptured()
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
                        new(Severity.Ok, "repo:Test", "All good"),
                        new(Severity.Ok, "repo:Test", "metric", metricName: "veeam_test", metricValue: 42.0)
                    }, server: "server1")
            };

            state.ProcessResults(results);

            var json = File.ReadAllText(statePath);
            var doc = JsonDocument.Parse(json);
            var captured = doc.RootElement.GetProperty("captured_errors");
            Assert.Empty(captured.EnumerateObject());
        }
        finally
        {
            if (File.Exists(statePath)) File.Delete(statePath);
        }
    }

    [Fact]
    public void SuppressedFindingHasSuppressedDetailKey()
    {
        var statePath = GetTempStatePath();
        try
        {
            var suppressions = new List<string> { "Low space" };
            var state = new FindingState(statePath, suppressions);
            var finding = new Finding(Severity.Warning, "repo:Test", "Low space on repository");
            var results = new List<MonitorResult>
            {
                new(MonitorType.RepoHealth, DateTime.UtcNow, 100, Severity.Warning,
                    new List<Finding> { finding }, server: "server1")
            };

            state.ProcessResults(results);

            // The finding object should have _suppressed detail set even though it's not emitted
            Assert.True(finding.Details.ContainsKey("_suppressed"));
            Assert.Equal(true, finding.Details["_suppressed"]);
        }
        finally
        {
            if (File.Exists(statePath)) File.Delete(statePath);
        }
    }

    [Fact]
    public void GetLastSuccessTimeReturnsNullWhenNoEntry()
    {
        var statePath = GetTempStatePath();
        try
        {
            var state = new FindingState(statePath);
            var result = state.GetLastSuccessTime("server1", "repo_health");
            Assert.Null(result);
        }
        finally
        {
            if (File.Exists(statePath)) File.Delete(statePath);
        }
    }

    [Fact]
    public void SetAndGetLastSuccessTimeRoundTrips()
    {
        var statePath = GetTempStatePath();
        try
        {
            var state = new FindingState(statePath);
            var now = DateTime.UtcNow;
            state.SetLastSuccessTime("server1", "repo_health", now);

            // Re-load from disk
            var state2 = new FindingState(statePath);
            var result = state2.GetLastSuccessTime("server1", "repo_health");

            Assert.NotNull(result);
            // Allow 1 second tolerance for serialization rounding
            Assert.True(Math.Abs((result.Value - now).TotalSeconds) < 1,
                $"Expected ~{now:O} but got {result.Value:O}");
        }
        finally
        {
            if (File.Exists(statePath)) File.Delete(statePath);
        }
    }

    [Fact]
    public void LastSuccessTimePersistedAsIso8601InJson()
    {
        var statePath = GetTempStatePath();
        try
        {
            var state = new FindingState(statePath);
            var now = new DateTime(2026, 4, 15, 10, 23, 27, DateTimeKind.Utc);
            state.SetLastSuccessTime("vbr01", "repo_health", now);

            var json = File.ReadAllText(statePath);
            var doc = JsonDocument.Parse(json);
            var lastSuccess = doc.RootElement.GetProperty("lastSuccess");
            var value = lastSuccess.GetProperty("vbr01:repo_health").GetString();

            Assert.NotNull(value);
            Assert.Contains("2026-04-15", value);
            Assert.Contains("10:23:27", value);
        }
        finally
        {
            if (File.Exists(statePath)) File.Delete(statePath);
        }
    }

    [Fact]
    public void LastSuccessTimeSeparatePerServerAndMonitor()
    {
        var statePath = GetTempStatePath();
        try
        {
            var state = new FindingState(statePath);
            var time1 = new DateTime(2026, 4, 15, 8, 0, 0, DateTimeKind.Utc);
            var time2 = new DateTime(2026, 4, 15, 9, 0, 0, DateTimeKind.Utc);
            var time3 = new DateTime(2026, 4, 15, 10, 0, 0, DateTimeKind.Utc);

            state.SetLastSuccessTime("server1", "repo_health", time1);
            state.SetLastSuccessTime("server1", "retention", time2);
            state.SetLastSuccessTime("server2", "repo_health", time3);

            var state2 = new FindingState(statePath);
            var r1 = state2.GetLastSuccessTime("server1", "repo_health");
            var r2 = state2.GetLastSuccessTime("server1", "retention");
            var r3 = state2.GetLastSuccessTime("server2", "repo_health");

            Assert.NotNull(r1);
            Assert.NotNull(r2);
            Assert.NotNull(r3);
            Assert.True(Math.Abs((r1.Value - time1).TotalSeconds) < 1);
            Assert.True(Math.Abs((r2.Value - time2).TotalSeconds) < 1);
            Assert.True(Math.Abs((r3.Value - time3).TotalSeconds) < 1);
        }
        finally
        {
            if (File.Exists(statePath)) File.Delete(statePath);
        }
    }

    [Fact]
    public void LastSuccessTimeDoesNotAffectExistingState()
    {
        var statePath = GetTempStatePath();
        try
        {
            // Create state with a finding first
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

            // Now set last success
            state.SetLastSuccessTime("server1", "repo_health", DateTime.UtcNow);

            // Reload and verify both exist
            var json = File.ReadAllText(statePath);
            var doc = JsonDocument.Parse(json);
            Assert.True(doc.RootElement.TryGetProperty("findings", out _));
            Assert.True(doc.RootElement.TryGetProperty("lastSuccess", out _));
            Assert.True(doc.RootElement.TryGetProperty("captured_errors", out _));
        }
        finally
        {
            if (File.Exists(statePath)) File.Delete(statePath);
        }
    }
}
