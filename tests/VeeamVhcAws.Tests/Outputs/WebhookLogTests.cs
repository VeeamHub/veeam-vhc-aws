using System.Net;
using System.Net.Sockets;
using System.Reflection;
using VeeamVhcAws.Core.Logging;
using VeeamVhcAws.Core.Models;
using VeeamVhcAws.Outputs;
using Xunit;

namespace VeeamVhcAws.Tests.Outputs;

public class WebhookLogTests
{
    [Fact]
    public void RedactSensitive_HasNoMethod()
    {
        var method = typeof(LoggingSetup).GetMethod(
            "RedactSensitive",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        Assert.Null(method);
    }

    [Fact]
    public void Skip_LogsTheHostOnly()
    {
        var handler = new WebhookHandler(
            "https://hooks.example.com/services/T123/SECRET", "generic", "critical");
        handler.Emit(Sample(Severity.Warning));

        Assert.Equal(
            "Webhook skipped for hooks.example.com — severity below threshold (Critical)",
            string.Join("\n", handler.Logs));
    }

    [Fact]
    public void Failure_LogsTheHostOnly()
    {
        var handler = new WebhookHandler("http://127.0.0.1:1/services/T123/SECRET", "generic", "ok");
        handler.Emit(Sample(Severity.Warning));

        var text = string.Join("\n", handler.Logs);
        Assert.Equal("Webhook rejected for 127.0.0.1: loopback", text);
        Assert.DoesNotContain("SECRET", text);
        Assert.DoesNotContain("T123", text);
    }

    [Fact]
    public async Task Success_LogsTheHostOnly()
    {
        using var listener = LoopbackHttpListener.Start(out var port);
        var serve = Task.Run(() =>
        {
            var ctx = listener.GetContext();
            ctx.Response.StatusCode = 204;
            ctx.Response.Close();
        });

        try
        {
            var handler = new WebhookHandler(
                $"http://127.0.0.1:{port}/services/T123/SECRET", "generic", "ok", allowLoopback: true);
            handler.Emit(Sample(Severity.Warning));
            var finished = await Task.WhenAny(serve, Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.Same(serve, finished);
            Assert.Equal(
                "Webhook sent to 127.0.0.1 (template=generic, status=204)",
                string.Join("\n", handler.Logs));
        }
        finally
        {
            listener.Stop();
        }
    }

    private static List<MonitorResult> Sample(Severity severity) =>
        new()
        {
            new(MonitorType.RepoHealth, DateTime.UtcNow, 10, severity,
                new List<Finding> { new(severity, "repo:Test", "Test finding message") },
                server: "test-server")
        };
}
