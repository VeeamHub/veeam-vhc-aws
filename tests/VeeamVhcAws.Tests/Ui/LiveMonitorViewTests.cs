using VeeamVhcAws.Core.Models;
using VeeamVhcAws.Ui;
using Xunit;

namespace VeeamVhcAws.Tests.Ui;

/// <summary>
/// Parallel servers report progress from worker threads. Those reports must not touch the
/// Spectre table, which the live display renders on its own thread. Reports are queued under a
/// lock and applied by the live-view thread before each refresh.
/// </summary>
public class LiveMonitorViewTests
{
    private static MonitorProgressEvent Event(string server, MonitorProgressStatus status, Severity sev = Severity.Ok) => new()
    {
        Server = server,
        Monitor = "repo_health",
        Status = status,
        Result = status == MonitorProgressStatus.Completed
            ? new MonitorResult(MonitorType.RepoHealth, DateTime.UtcNow, 1, sev, new List<Finding>(), server: server)
            : null,
    };

    [Fact]
    public async Task Report_FromAWorkerThread_DoesNotMutateTheTable()
    {
        using var view = LiveMonitorView.CreateDetached(new[] { ("s1", "repo_health") });

        await Task.Run(() => view.Report(Event("s1", MonitorProgressStatus.Running)));

        Assert.Contains("Queued", view.RenderText());
        Assert.DoesNotContain("Running", view.RenderText());
    }

    [Fact]
    public async Task ApplyPending_OnTheViewThread_ShowsTheLatestStatus()
    {
        using var view = LiveMonitorView.CreateDetached(new[] { ("s1", "repo_health"), ("s2", "repo_health") });

        await Task.Run(() =>
        {
            view.Report(Event("s1", MonitorProgressStatus.Running));
            view.Report(Event("s1", MonitorProgressStatus.Completed));
            view.Report(Event("s2", MonitorProgressStatus.Failed));
        });

        Assert.True(view.ApplyPending());
        var text = view.RenderText();
        Assert.Contains("Done — OK", text);
        Assert.Contains("Failed", text);
        Assert.DoesNotContain("Running", text);
        Assert.False(view.ApplyPending()); // nothing new to apply
    }

    [Fact]
    public async Task ConcurrentReports_WhileTheViewRenders_DoNotThrow()
    {
        var work = Enumerable.Range(0, 20).Select(i => ($"s{i}", "repo_health")).ToArray();
        using var view = LiveMonitorView.CreateDetached(work);
        using var stop = new CancellationTokenSource();

        var renderer = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                view.ApplyPending();
                view.RenderText();
                Thread.Sleep(1); // like the live loop, don't spin a core and starve other tests
            }
        });

        await Task.WhenAll(work.Select(w => Task.Run(() =>
        {
            for (var n = 0; n < 50; n++)
            {
                view.Report(Event(w.Item1, MonitorProgressStatus.Running));
                view.Report(Event(w.Item1, MonitorProgressStatus.Completed, Severity.Warning));
            }
        })));
        stop.Cancel();
        await renderer;

        view.ApplyPending();
        Assert.DoesNotContain("Running", view.RenderText());
    }
}
