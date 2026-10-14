using VeeamVhcAws.Web.Components.Pages;

namespace VeeamVhcAws.UiTests;

public class ThresholdsPageTests
{
    [Fact]
    public void RepoSave_PersistsTheWarningThreshold()
    {
        using var ui = new UiHarness();
        var cut = ui.Context.RenderComponent<Thresholds>();

        UiHarness.AssertEveryControlHasName(cut);
        UiHarness.AssertNamed(cut, "thresh-free-warn", "Free space warning threshold (% free)");
        UiHarness.AssertNamed(cut, "thresh-free-crit", "Free space critical threshold (% free)");
        cut.Find("#thresh-free-warn").Change("17");
        UiHarness.Click(cut, "Save Repo Health");

        Assert.Contains("Repo Health thresholds saved", cut.Markup);
        Assert.Contains("free_space_warning_pct: 17", File.ReadAllText(ui.ConfigPath));

        var again = ui.Context.RenderComponent<Thresholds>();
        Assert.Equal("17", again.Find("#thresh-free-warn").GetAttribute("value"));
    }

    [Fact]
    public void RetentionTab_SavesWithoutTouchingRepo()
    {
        using var ui = new UiHarness();
        var cut = ui.Context.RenderComponent<Thresholds>();
        UiHarness.Click(cut, "Retention");
        UiHarness.AssertNamed(cut, "thresh-overage", "Overage multiplier (restore points beyond policy × this = warning)");
        cut.Find("#thresh-exclude").Change("archive-2023");
        UiHarness.Click(cut, "Save Retention");

        Assert.Contains("Retention thresholds saved", cut.Markup);
        Assert.Contains("archive-2023", File.ReadAllText(ui.ConfigPath));
        UiHarness.Click(cut, "Repo Health");
        Assert.Equal("15", cut.Find("#thresh-free-warn").GetAttribute("value"));
    }

    [Fact]
    public void WorkerTab_NamesFailureFields()
    {
        using var ui = new UiHarness();
        var cut = ui.Context.RenderComponent<Thresholds>();
        UiHarness.Click(cut, "Worker Health");
        UiHarness.AssertEveryControlHasName(cut);
        UiHarness.AssertNamed(cut, "thresh-failure-warn", "Session failure rate — warning threshold");
        UiHarness.Click(cut, "Save Worker Health");
        Assert.Contains("Worker Health thresholds saved", cut.Markup);
        Assert.Contains("session_failure_rate_warning:", File.ReadAllText(ui.ConfigPath));
    }
}
