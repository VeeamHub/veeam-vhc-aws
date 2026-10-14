using VeeamVhcAws.Web.Components.Pages;

namespace VeeamVhcAws.UiTests;

public class DashboardPageTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("null")]
    public void EmptyLastRun_SaysNoRunsYet(string? lastRun)
    {
        var state = lastRun is null
            ? "{\"findings\":{},\"captured_errors\":{},\"suppressions\":{}}"
            : "{\"last_run\":\"" + lastRun + "\",\"findings\":{},\"captured_errors\":{},\"suppressions\":{}}";
        using var ui = new UiHarness(state);
        var cut = ui.Context.RenderComponent<Dashboard>();

        Assert.Contains("No runs yet — 0 server(s) configured", cut.Markup);
        Assert.Contains("no runs yet", cut.Markup);
        Assert.DoesNotContain("All monitors clear", cut.Markup);
    }

    [Fact]
    public void MissingStateFile_SaysNoRunsYet()
    {
        using var ui = new UiHarness();
        var cut = ui.Context.RenderComponent<Dashboard>();

        Assert.Contains("No runs yet — 0 server(s) configured", cut.Markup);
        Assert.Contains("Add a server", cut.Markup);
        Assert.DoesNotContain("All monitors clear", cut.Markup);
    }

    [Fact]
    public void CompletedRunWithNoFindings_SaysAllMonitorsClear()
    {
        using var ui = new UiHarness("""
            {"last_run":"2026-10-13T12:00:00Z","findings":{},"captured_errors":{},"suppressions":{}}
            """);
        var cut = ui.Context.RenderComponent<Dashboard>();

        Assert.Contains("All monitors clear — 0 server(s), 0 findings", cut.Markup);
        Assert.DoesNotContain("No runs yet", cut.Markup);
    }

    [Fact]
    public void FindingsWithoutARun_AskForAction()
    {
        using var ui = new UiHarness("""
            {
              "findings": {
                "abc": {"severity":"warning","server":"lab-vbr","monitor":"repo_health","resource":"repo-disk","message":"low","count":1}
              },
              "captured_errors": {},
              "suppressions": {}
            }
            """);
        var cut = ui.Context.RenderComponent<Dashboard>();

        Assert.Contains("1 warning", cut.Markup);
        Assert.Contains("action required", cut.Markup);
        Assert.DoesNotContain("All monitors clear", cut.Markup);
    }
}
