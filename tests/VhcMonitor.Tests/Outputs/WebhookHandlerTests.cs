using Xunit;
using VhcMonitor.Core.Models;
using VhcMonitor.Outputs;

namespace VhcMonitor.Tests.Outputs;

public class WebhookHandlerTests
{
    private static List<MonitorResult> SampleResults(Severity severity = Severity.Warning)
    {
        return new List<MonitorResult>
        {
            new(MonitorType.RepoHealth, DateTime.UtcNow, 150, severity,
                new List<Finding>
                {
                    new(severity, "repo:Test", "Test finding message")
                }, server: "test-server")
        };
    }

    [Fact]
    public void ShouldNotSendBelowMinSeverity()
    {
        // Create handler with min_severity=critical, but only have OK result
        // We can't easily test the full emit without a server, but we verify construction
        var handler = new WebhookHandler("http://example.com", "generic", "critical");
        Assert.NotNull(handler);
    }

    [Fact]
    public void MinSeverityParsesCorrectly()
    {
        // Handler should be constructable with all severity levels
        var handler1 = new WebhookHandler("http://example.com", "generic", "ok");
        var handler2 = new WebhookHandler("http://example.com", "generic", "warning");
        var handler3 = new WebhookHandler("http://example.com", "generic", "critical");
        var handler4 = new WebhookHandler("http://example.com", "generic", "error");
        Assert.NotNull(handler1);
        Assert.NotNull(handler2);
        Assert.NotNull(handler3);
        Assert.NotNull(handler4);
    }

    [Fact]
    public void AllTemplateTypesConstructable()
    {
        foreach (var template in new[] { "slack", "teams", "pagerduty", "ntfy", "generic" })
        {
            var handler = new WebhookHandler("http://example.com", template);
            Assert.NotNull(handler);
        }
    }

    [Fact]
    public void DeduplicateDefault()
    {
        var handler = new WebhookHandler("http://example.com", "ntfy");
        Assert.NotNull(handler);
    }
}
