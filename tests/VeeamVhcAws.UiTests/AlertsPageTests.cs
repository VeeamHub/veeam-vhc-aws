using VeeamVhcAws.Web.Components.Pages;

namespace VeeamVhcAws.UiTests;

public class AlertsPageTests
{
    [Fact]
    public void Empty_SaysNoAlertsYet()
    {
        using var ui = new UiHarness();
        var cut = ui.Context.Render<Alerts>();

        UiHarness.AssertEveryControlHasName(cut);
        UiHarness.AssertNamed(cut, "alerts-search", "Search:");
        Assert.Contains("No alerts yet.", cut.Markup);
        Assert.DoesNotContain("No alerts match the current filters.", cut.Markup);
    }

    [Fact]
    public void SearchMiss_SaysNothingMatches()
    {
        using var ui = new UiHarness(OneFinding("abcdef1234567890"));
        var cut = ui.Context.Render<Alerts>();
        cut.Find("#alerts-search").Input("zzzz-not-here");

        Assert.Contains("No alerts match the current filters.", cut.Markup);
        Assert.DoesNotContain("No alerts yet.", cut.Markup);
    }

    [Fact]
    public void Suppress_WorksForAFindingThatWasNeverCaptured()
    {
        using var ui = new UiHarness(OneFinding("abc"));
        var cut = ui.Context.Render<Alerts>();

        Assert.Contains("repo-disk", cut.Markup);
        UiHarness.Click(cut, "Suppress repo-disk");

        Assert.Contains("Suppressed repo-disk", cut.Markup);
        Assert.Contains("Unsuppress repo-disk", cut.Markup);
        Assert.True(UiHarness.HasSuppression(ui.StatePath, "abc"));

        var again = ui.Context.Render<Alerts>();
        Assert.Contains("Unsuppress repo-disk", again.Markup);
        UiHarness.Click(again, "Unsuppress repo-disk");
        Assert.Contains("Unsuppressed repo-disk", again.Markup);
        Assert.Contains("Suppress repo-disk", again.Markup);
        Assert.False(UiHarness.HasSuppression(ui.StatePath, "abc"));
    }

    [Fact]
    public void DuplicateResources_GetDistinctSuppressNames()
    {
        using var ui = new UiHarness("""
            {
              "findings": {
                "abc": {"severity":"warning","server":"lab-vbr","monitor":"repo_health","resource":"repo-disk","message":"one","count":1},
                "defghijk": {"severity":"warning","server":"lab-vbr","monitor":"repo_health","resource":"repo-disk","message":"two","count":1}
              },
              "captured_errors": {},
              "suppressions": {}
            }
            """);
        var cut = ui.Context.Render<Alerts>();
        var names = cut.FindAll("button").Select(b => b.TextContent.Trim()).Where(t => t.StartsWith("Suppress ")).OrderBy(t => t).ToArray();
        Assert.Equal(new[] { "Suppress repo-disk (abc)", "Suppress repo-disk (defghijk)" }, names);
    }

    private static string OneFinding(string key) =>
        "{\"findings\":{\"" + key + "\":{\"severity\":\"warning\",\"server\":\"lab-vbr\",\"monitor\":\"repo_health\",\"resource\":\"repo-disk\",\"message\":\"free space low\",\"count\":1}},\"captured_errors\":{},\"suppressions\":{}}";
}
