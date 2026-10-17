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
    public void RetentionSave_KeepsYamlOnlyExcludeJobs()
    {
        using var ui = new UiHarness();
        File.WriteAllText(ui.ConfigPath, """
            retention:
              enabled: true
              exclude_jobs:
                - noisy-job
              exclude_backups:
                - old-backup
              thresholds:
                overage_multiplier: 1.5
                max_age_multiplier: 1.5
                orphan_detection: true
              session_lookback_hours: 24
            """);
        var cut = ui.Context.RenderComponent<Thresholds>();
        UiHarness.Click(cut, "Retention");
        UiHarness.Click(cut, "Save Retention");

        var yaml = File.ReadAllText(ui.ConfigPath);
        Assert.Contains("noisy-job", yaml);
        Assert.Contains("old-backup", yaml);
    }

    [Fact]
    public void WorkerSave_KeepsPerJobSessions()
    {
        using var ui = new UiHarness();
        File.WriteAllText(ui.ConfigPath, """
            worker_health:
              enabled: true
              lookback_hours: 24
              per_job_sessions: true
              thresholds:
                session_failure_rate_warning: 0.1
                session_failure_rate_critical: 0.3
                retention_session_max_failures: 1
                zero_deleted_items_warning: true
                recurring_failure_threshold: 3
            """);
        var cut = ui.Context.RenderComponent<Thresholds>();
        UiHarness.Click(cut, "Worker Health");
        UiHarness.Click(cut, "Save Worker Health");

        Assert.Contains("per_job_sessions: true", File.ReadAllText(ui.ConfigPath));
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
