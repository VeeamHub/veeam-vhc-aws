using VeeamVhcAws.Web.Components.Pages;

namespace VeeamVhcAws.UiTests;

public class SettingsPageTests
{
    [Fact]
    public void FieldsAreNamed_AndSaveSurvivesReload()
    {
        using var ui = new UiHarness();
        var cut = ui.Context.RenderComponent<Settings>();

        UiHarness.AssertEveryControlHasName(cut);
        UiHarness.AssertNamed(cut, "settings-log-level", "Log level");
        UiHarness.AssertNamed(cut, "settings-log-file", "Log file path");
        UiHarness.AssertNamed(cut, "settings-timeout", "Request timeout (seconds)");
        UiHarness.AssertNamed(cut, "settings-session-timeout", "Session timeout — /api/v1/sessions (seconds) raise for very large deployments");
        Assert.Contains("Also log to console (stderr)", cut.Find("input[type=checkbox]").ParentElement!.TextContent);

        cut.Find("#settings-log-level").Change("INFO");
        cut.Find("#settings-log-file").Change("./ops.log");
        UiHarness.Click(cut, "Save Settings");
        Assert.Contains("Settings saved", cut.Markup);

        var again = ui.Context.RenderComponent<Settings>();
        var selected = again.FindAll("#settings-log-level option").Single(o => o.HasAttribute("selected"));
        Assert.Equal("INFO", selected.GetAttribute("value"));
        Assert.Equal("./ops.log", again.Find("#settings-log-file").GetAttribute("value"));
        var yaml = File.ReadAllText(ui.ConfigPath);
        Assert.Contains("INFO", yaml);
        Assert.Contains("./ops.log", yaml);
    }
}
