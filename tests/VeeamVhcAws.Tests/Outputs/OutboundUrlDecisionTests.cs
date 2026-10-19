using System.Net;
using System.Net.Sockets;
using VeeamVhcAws.Core.Models;
using VeeamVhcAws.Core.Output;
using VeeamVhcAws.Outputs;
using Xunit;

namespace VeeamVhcAws.Tests.Outputs;

public class OutboundUrlDecisionTests
{
    [Theory]
    [InlineData("http://10.1.2.3/hook", false, true, "10.1.2.3", "")]
    [InlineData("http://192.168.1.9/hook", false, true, "192.168.1.9", "")]
    [InlineData("http://172.16.5.5/hook", false, true, "172.16.5.5", "")]
    [InlineData("http://172.31.255.255/hook", false, true, "172.31.255.255", "")]
    [InlineData("https://1.2.3.4/hook", false, true, "1.2.3.4", "")]
    [InlineData("file:///etc/passwd", false, false, "invalid", "scheme")]
    [InlineData("ftp://10.1.2.3/hook", false, false, "10.1.2.3", "scheme")]
    [InlineData("http://169.254.169.254/latest/meta-data", false, false, "169.254.169.254", "metadata")]
    [InlineData("http://169.254.1.1/", false, false, "169.254.1.1", "link-local")]
    [InlineData("http://[fe80::1]/", false, false, "[fe80::1]", "link-local")]
    [InlineData("http://0.0.0.0/", false, false, "0.0.0.0", "unspecified")]
    [InlineData("http://[::]/", false, false, "[::]", "unspecified")]
    [InlineData("http://127.0.0.1/secret", false, false, "127.0.0.1", "loopback")]
    [InlineData("http://127.0.0.1/secret", true, true, "127.0.0.1", "")]
    [InlineData("http://[::1]/secret", false, false, "[::1]", "loopback")]
    [InlineData("http://[::ffff:169.254.169.254]/", false, false, "[::ffff:169.254.169.254]", "metadata")]
    public void Decide_ReturnsTheLiteralResult(string url, bool allowLoopback, bool allowed, string host, string reason)
    {
        var decision = OutboundUrlDecision.Decide(url, allowLoopback);
        Assert.Equal(allowed, decision.Allowed);
        Assert.Equal(host, decision.Host);
        Assert.Equal(reason, decision.Reason);
    }

    [Fact]
    public void Localhost_IsLoopbackUnlessThatOutputAllowsIt()
    {
        var blocked = OutboundUrlDecision.Decide("http://localhost/secret", allowLoopback: false);
        Assert.False(blocked.Allowed);
        Assert.Equal("localhost", blocked.Host);
        Assert.Equal("loopback", blocked.Reason);

        var allowed = OutboundUrlDecision.Decide("http://localhost/secret", allowLoopback: true);
        Assert.True(allowed.Allowed);
        Assert.Equal("localhost", allowed.Host);
        Assert.Equal("", allowed.Reason);
    }

    [Fact]
    public async Task Webhook_DoesNotCallLoopbackUnlessAllowed()
    {
        var hits = 0;
        using var listener = LoopbackHttpListener.Start(out var port);
        var serve = Task.Run(() =>
        {
            while (listener.IsListening)
            {
                var ctx = listener.GetContext();
                Interlocked.Increment(ref hits);
                ctx.Response.StatusCode = 204;
                ctx.Response.Close();
            }
        });

        try
        {
            var blocked = new WebhookHandler(
                $"http://127.0.0.1:{port}/services/T123/SECRET", "generic", "ok", true, false);
            blocked.Emit(Sample());
            await Task.Delay(200);
            Assert.Equal(0, hits);
            Assert.Equal(
                "Webhook rejected for 127.0.0.1: loopback",
                string.Join("\n", blocked.Logs));

            var allowed = new WebhookHandler(
                $"http://127.0.0.1:{port}/services/T123/SECRET", "generic", "ok", true, true);
            allowed.Emit(Sample());
            await Task.Delay(500);
            Assert.Equal(1, Volatile.Read(ref hits));
            Assert.DoesNotContain("SECRET", string.Join("\n", allowed.Logs));
        }
        finally
        {
            listener.StopQuietly();
            try { await serve; } catch (HttpListenerException) { }
        }
    }

    [Fact]
    public async Task Webhook_DoesNotFollowARedirect()
    {
        var hits = 0;
        using var listener = LoopbackHttpListener.Start(out var port);
        var serve = Task.Run(() =>
        {
            var ctx = listener.GetContext();
            Interlocked.Increment(ref hits);
            ctx.Response.StatusCode = 302;
            ctx.Response.RedirectLocation = "http://169.254.169.254/latest/meta-data";
            ctx.Response.Close();
        });

        try
        {
            var handler = new WebhookHandler(
                $"http://127.0.0.1:{port}/hook", "generic", "ok", true, true);
            handler.Emit(Sample());
            var finished = await Task.WhenAny(serve, Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.Same(serve, finished);
            Assert.Equal(1, hits);
            var text = string.Join("\n", handler.Logs);
            Assert.Contains("302", text);
            Assert.DoesNotContain("169.254.169.254", text);
        }
        finally
        {
            listener.StopQuietly();
        }
    }

    [Fact]
    public void Pushgateway_RejectsMetadata()
    {
        var handler = new PrometheusHandler(
            "pushgateway", "http://169.254.169.254/metrics", "veeam_vhc_aws", 9100, false);
        handler.Emit(Sample());
        Assert.Equal(
            "Pushgateway rejected for 169.254.169.254: metadata",
            string.Join("\n", handler.Logs));
    }

    private static List<MonitorResult> Sample() =>
        new()
        {
            new(MonitorType.RepoHealth, DateTime.UtcNow, 10, Severity.Warning,
                new List<Finding> { new(Severity.Warning, "repo:Test", "Test finding message") },
                server: "test-server")
        };
}
