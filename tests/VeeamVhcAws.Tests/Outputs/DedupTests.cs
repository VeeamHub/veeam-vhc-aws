using VeeamVhcAws.Core.Models;
using VeeamVhcAws.Core.Output;
using VeeamVhcAws.Outputs;
using Xunit;

namespace VeeamVhcAws.Tests.Outputs;

/// <summary>
/// README "Alert Deduplication": a problem that persists on the next run is silent, and
/// deduplication is on by default. That has to hold for every alert output, not only ntfy.
/// </summary>
public class DedupTests
{
    // .invalid never resolves, so a send that is not skipped fails fast without leaving the box.
    private const string Url = "https://hooks.example.invalid/services/T123";
    private const string DedupSkip = "Webhook skipped for hooks.example.invalid — no new or resolved findings (dedup)";

    private static Finding Persisting(Severity sev) =>
        new(sev, "repo:demo", "Repository is low on space")
        {
            Details = new Dictionary<string, object> { ["_seen_before"] = true },
        };

    private static MonitorResult Result(Severity sev, Finding finding) =>
        new(MonitorType.RepoHealth, DateTime.UtcNow, 10, sev, new List<Finding> { finding }, server: "s1");

    [Theory]
    [InlineData("generic")]
    [InlineData("slack")]
    [InlineData("teams")]
    [InlineData("pagerduty")]
    [InlineData("ntfy")]
    public void Webhook_PersistingFinding_IsSkipped_ForEveryTemplate(string template)
    {
        var handler = new WebhookHandler(Url, template, "warning");

        handler.Emit(new[] { Result(Severity.Critical, Persisting(Severity.Critical)) });

        Assert.Equal(DedupSkip, string.Join("\n", handler.Logs));
    }

    [Theory]
    [InlineData("generic")]
    [InlineData("slack")]
    public void Webhook_NewFinding_IsNotSkippedByDedup(string template)
    {
        var handler = new WebhookHandler(Url, template, "warning");

        handler.Emit(new[] { Result(Severity.Critical, new Finding(Severity.Critical, "repo:demo", "new")) });

        Assert.DoesNotContain(handler.Logs, l => l.Contains("(dedup)"));
    }

    [Theory]
    [InlineData("generic")]
    [InlineData("slack")]
    public void Webhook_ResolvedFinding_IsNotSkippedByDedup(string template)
    {
        var handler = new WebhookHandler(Url, template, "ok");
        var resolved = new Finding(Severity.Ok, "repo:demo", "RESOLVED: Repository is low on space");

        handler.Emit(new[] { Result(Severity.Ok, resolved) });

        Assert.DoesNotContain(handler.Logs, l => l.Contains("(dedup)"));
    }

    [Fact]
    public void Webhook_DeduplicateFalse_SendsPersistingFinding()
    {
        var handler = new WebhookHandler(Url, "slack", "warning", deduplicate: false);

        handler.Emit(new[] { Result(Severity.Critical, Persisting(Severity.Critical)) });

        Assert.DoesNotContain(handler.Logs, l => l.Contains("(dedup)"));
    }

    [Fact]
    public void Email_PersistingFinding_IsSkippedByDefault()
    {
        var handler = new EmailHandler("smtp.test", 587, "from@test.local",
            new List<string> { "to@test.local" }, "critical");

        var send = handler.ShouldEmit(new[] { Result(Severity.Critical, Persisting(Severity.Critical)) }, out var reason);

        Assert.False(send);
        Assert.Equal("no new or resolved findings (dedup)", reason);
    }

    [Fact]
    public void Email_DeduplicateFalseInConfig_SendsPersistingFinding()
    {
        var config = new Dictionary<string, object>
        {
            ["output"] = new List<object>
            {
                new Dictionary<object, object>
                {
                    ["type"] = "email",
                    ["smtp_host"] = "smtp.test",
                    ["from_addr"] = "from@test.local",
                    ["to_addrs"] = "to@test.local",
                    ["deduplicate"] = false,
                },
            },
        };
        var handler = Assert.IsType<EmailHandler>(
            Assert.Single(OutputHandlerFactory.CreateHandlers(config, Path.Combine(Path.GetTempPath(), "c.yaml"))));

        var send = handler.ShouldEmit(new[] { Result(Severity.Critical, Persisting(Severity.Critical)) }, out _);

        Assert.True(send);
    }

    [Fact]
    public void Email_Summary_IgnoresDedup()
    {
        var handler = new EmailHandler("smtp.test", 587, "from@test.local",
            new List<string> { "to@test.local" }, "critical");
        var result = Result(Severity.Critical, Persisting(Severity.Critical));
        result.Metadata["summary"] = true;

        Assert.True(handler.ShouldEmit(new[] { result }, out _));
    }
}
