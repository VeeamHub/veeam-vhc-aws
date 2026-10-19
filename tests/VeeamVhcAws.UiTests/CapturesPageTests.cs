using System.Text.Json;
using VeeamVhcAws.Web.Components.Pages;

namespace VeeamVhcAws.UiTests;

public class CapturesPageTests
{
    [Fact]
    public void Empty_SaysNoCapturedErrorsYet()
    {
        using var ui = new UiHarness();
        var cut = ui.Context.Render<Captures>();

        UiHarness.AssertEveryControlHasName(cut);
        Assert.Contains("No captured errors yet.", cut.Markup);
        Assert.DoesNotContain("No captured errors match the current filter.", cut.Markup);
    }

    [Fact]
    public void ShortKey_Suppresses_AndFilterEmptyStateIsSpecific()
    {
        using var ui = new UiHarness("""
            {
              "findings": {},
              "captured_errors": {
                "abc": {"severity":"error","server":"lab-vbr","monitor":"repo_health","resource":"repo-disk","message":"disk full","count":2,"suppressed":false}
              },
              "suppressions": {}
            }
            """);
        var cut = ui.Context.Render<Captures>();
        Assert.Equal("abc", cut.Find("td.key").TextContent.Trim());
        UiHarness.Click(cut, "Suppress repo-disk");
        Assert.Contains("Suppressed repo-disk", cut.Markup);
        Assert.True(UiHarness.HasSuppression(ui.StatePath, "abc"));

        cut.Find("#captures-filter").Change("unsuppressed");
        Assert.Contains("No captured errors match the current filter.", cut.Markup);
        Assert.DoesNotContain("No captured errors yet.", cut.Markup);
    }

    [Fact]
    public void Unsuppress_ClearsAStaleCapturedFlag()
    {
        using var ui = new UiHarness("""
            {
              "findings": {},
              "captured_errors": {
                "abc": {"severity":"error","server":"lab-vbr","monitor":"repo_health","resource":"repo-disk","message":"disk full","count":2,"suppressed":true}
              },
              "suppressions": {}
            }
            """);
        var cut = ui.Context.Render<Captures>();
        UiHarness.Click(cut, "Unsuppress repo-disk");

        Assert.Contains("Unsuppressed repo-disk", cut.Markup);
        Assert.Contains("Suppress repo-disk", cut.Markup);
        using var doc = JsonDocument.Parse(File.ReadAllText(ui.StatePath));
        var suppressed = doc.RootElement.GetProperty("captured_errors").GetProperty("abc").GetProperty("suppressed").GetBoolean();
        Assert.False(suppressed);
    }
}
