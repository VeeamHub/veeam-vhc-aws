using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using VeeamVhcAws.Core.Models;
using VeeamVhcAws.Outputs;
using Xunit;

namespace VeeamVhcAws.Tests.Outputs;

/// <summary>
/// Pins the exact bytes each webhook template puts on the wire, alert and summary.
/// Golden files live in Fixtures/Webhook. To regenerate after an intended payload change,
/// run with VHC_UPDATE_WEBHOOK_GOLDENS=1 and review the diff.
/// </summary>
public class WebhookPayloadTests
{
    private const string DatePlaceholder = "{{DATE}}";
    private static readonly DateTime Ts = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    public static IEnumerable<object[]> AlertTemplates =>
        new[] { "generic", "slack", "teams", "pagerduty", "ntfy" }.Select(t => new object[] { t });

    [Theory]
    [MemberData(nameof(AlertTemplates))]
    public async Task Alert_PayloadMatchesGolden(string template)
    {
        var sent = await Capture(template, MixedResults(summary: false));
        AssertGolden($"alert-{template}", sent);
    }

    [Theory]
    [MemberData(nameof(AlertTemplates))]
    public async Task Summary_PayloadMatchesGolden(string template)
    {
        var sent = await Capture(template, MixedResults(summary: true));
        AssertGolden($"summary-{template}", sent);
    }

    [Fact]
    public async Task NtfyAlert_AllOk_WithoutDedup()
    {
        var results = new List<MonitorResult>
        {
            new(MonitorType.RepoHealth, Ts, 5, Severity.Ok, new List<Finding>(), server: "vbr1"),
            new(MonitorType.Retention, Ts, 5, Severity.Ok, new List<Finding>()),
        };
        var sent = await Capture("ntfy", results, deduplicate: false);
        AssertGolden("alert-ntfy-all-ok", sent);
    }

    [Fact]
    public async Task NtfyAlert_CapsFindingsPerMonitor()
    {
        var findings = Enumerable.Range(1, 17)
            .Select(i => new Finding(Severity.Warning, $"repo:R{i:D2}", $"Finding {i}"))
            .ToList();
        var results = new List<MonitorResult>
        {
            new(MonitorType.RepoHealth, Ts, 5, Severity.Warning, findings, server: "vbr1"),
        };
        var sent = await Capture("ntfy", results);
        AssertGolden("alert-ntfy-capped", sent);
    }

    [Fact]
    public async Task NtfySummary_TruncatesLongBody()
    {
        var findings = Enumerable.Range(1, 60)
            .Select(i => new Finding(Severity.Critical, $"repo:Repository-{i:D2}",
                $"Repository {i} has been unreachable for longer than the configured threshold"))
            .ToList();
        var results = new List<MonitorResult>
        {
            new(MonitorType.RepoHealth, Ts, 5, Severity.Critical, findings, server: "vbr1",
                metadata: new Dictionary<string, object> { ["summary"] = true }),
        };
        var sent = await Capture("ntfy", results);
        AssertGolden("summary-ntfy-truncated", sent);
    }

    /// <summary>
    /// One result set that walks every branch the formatters take: server-prefixed resources,
    /// metric findings, seen-before findings, RESOLVED findings, long Workloads messages,
    /// connection findings that suppress errors, an OK monitor, and cross-correlation.
    /// </summary>
    private static List<MonitorResult> MixedResults(bool summary)
    {
        var workloads = string.Join(", ", Enumerable.Range(1, 12).Select(i => $@"\syn01\docker vm-{i:D2}"));
        var repo = new MonitorResult(MonitorType.RepoHealth, Ts, 150, Severity.Critical,
            new List<Finding>
            {
                new(Severity.Warning, "[vbr1] repo:Primary", "Free space 8% is below the 10% threshold"),
                new(Severity.Critical, "repo:Archive", $"Backup failed for 12 workloads. Workloads: {workloads}"),
                new(Severity.Warning, "repo:Seen", "Already reported",
                    details: new Dictionary<string, object> { ["_seen_before"] = true }),
                new(Severity.Ok, "repo:Primary", "free_pct", metricName: "free_pct", metricValue: 8.0),
                new(Severity.Ok, "[vbr1] repo:Old", "RESOLVED: free space back above threshold"),
            },
            server: "vbr1",
            errors: new List<string> { "timeout talking to vbr1", new string('x', 130) },
            metadata: summary ? new Dictionary<string, object> { ["summary"] = true } : null);

        var worker = new MonitorResult(MonitorType.WorkerHealth, Ts, 75, Severity.Error,
            new List<Finding> { new(Severity.Error, "[vbaws1] connection", "Unable to connect to VBAWS") },
            server: "vbaws1",
            errors: new List<string> { "connection refused" });

        var retention = new MonitorResult(MonitorType.Retention, Ts, 20, Severity.Ok, new List<Finding>(), server: "vbr1");

        var correlation = new MonitorResult(MonitorType.CrossCorrelation, Ts, 3, Severity.Warning,
            new List<Finding> { new(Severity.Warning, "correlation", "Correlated failure across servers") });

        return new List<MonitorResult> { repo, worker, retention, correlation };
    }

    private sealed record Sent(string ContentType, string Headers, string Body)
    {
        public string Render() => $"Content-Type: {ContentType}\n{Headers}\n{Body}";
    }

    private static async Task<Sent> Capture(string template, List<MonitorResult> results, bool deduplicate = true)
    {
        using var listener = LoopbackHttpListener.Start(out var port);
        var serve = Task.Run(() =>
        {
            var ctx = listener.GetContext();
            using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
            var body = reader.ReadToEnd();
            var ntfyHeaders = new[] { "Title", "Priority", "Tags", "Markdown" }
                .Where(h => ctx.Request.Headers[h] != null)
                .Select(h => $"{h}: {ctx.Request.Headers[h]}");
            var sent = new Sent(ctx.Request.ContentType ?? "", string.Join("\n", ntfyHeaders), body);
            ctx.Response.StatusCode = 204;
            ctx.Response.Close();
            return sent;
        });

        try
        {
            var handler = new WebhookHandler($"http://127.0.0.1:{port}/hook", template, "ok",
                deduplicate: deduplicate, allowLoopback: true);
            handler.Emit(results);
            var finished = await Task.WhenAny(serve, Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.True(ReferenceEquals(serve, finished), $"no request received; logs: {string.Join(" | ", handler.Logs)}");
            return await serve;
        }
        finally
        {
            listener.StopQuietly();
        }
    }

    private static void AssertGolden(string name, Sent sent, [CallerFilePath] string callerPath = "")
    {
        // The daily summary embeds today's local date; swap it for a placeholder.
        var now = DateTime.Now;
        var actual = sent.Render().Replace($"{now:MMMM} {now.Day}, {now.Year}", DatePlaceholder);

        if (Environment.GetEnvironmentVariable("VHC_UPDATE_WEBHOOK_GOLDENS") == "1")
        {
            // Write into the source tree; the next build copies it to the output directory.
            var sourcePath = Path.Combine(Path.GetDirectoryName(callerPath)!, "..", "Fixtures", "Webhook", $"{name}.golden");
            Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
            File.WriteAllText(sourcePath, actual);
            return;
        }

        var goldenPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Webhook", $"{name}.golden");
        Assert.True(File.Exists(goldenPath), $"missing golden {goldenPath}");
        Assert.Equal(File.ReadAllText(goldenPath), actual);
    }
}
