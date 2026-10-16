using VeeamVhcAws.Scheduling;
using VeeamVhcAws.Web.Components.Pages;

namespace VeeamVhcAws.UiTests;

public class SettingsPageTests
{
    [Fact]
    public void FieldsAreNamed_AndSaveSurvivesReload()
    {
        using var ui = new UiHarness();
        var cut = ui.Context.Render<Settings>();

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

        var again = ui.Context.Render<Settings>();
        var selected = again.FindAll("#settings-log-level option").Single(o => o.HasAttribute("selected"));
        Assert.Equal("INFO", selected.GetAttribute("value"));
        Assert.Equal("./ops.log", again.Find("#settings-log-file").GetAttribute("value"));
        var yaml = File.ReadAllText(ui.ConfigPath);
        Assert.Contains("INFO", yaml);
        Assert.Contains("./ops.log", yaml);
        Assert.DoesNotContain("log_level:", yaml);
    }

    [Fact]
    public void Save_KeepsRotationKeysTheFormDoesNotEdit()
    {
        using var ui = new UiHarness();
        File.WriteAllText(ui.ConfigPath, """
            global:
              timeout_seconds: 30
              logging:
                level: DEBUG
                file: ./veeam-vhc-aws.log
                rotation_when: midnight
                rotation_interval: 1
                rotation_keep: 30
                console: false
                disk_warning_pct: 20
            """);
        var cut = ui.Context.Render<Settings>();
        UiHarness.Click(cut, "Save Settings");

        var yaml = File.ReadAllText(ui.ConfigPath);
        Assert.Contains("rotation_when: midnight", yaml);
        Assert.Contains("rotation_interval: 1", yaml);
    }

    [Fact]
    public void DailySummary_SaveRegistersAndRemoveUnregisters()
    {
        var host = new RecordingHost();
        using var ui = new UiHarness(tasks: host);
        var cut = ui.Context.Render<Settings>();

        cut.Find("#settings-daily-summary").Change(true);
        UiHarness.Click(cut, "Save Settings");

        Assert.Contains("Daily summary task registered.", cut.Markup);
        Assert.Contains("enabled: true", File.ReadAllText(ui.ConfigPath));
        Assert.Equal(new[] { "register 08:00 " + ui.ConfigPath }, host.Calls);

        cut.Find("#settings-daily-summary").Change(false);
        UiHarness.Click(cut, "Save Settings");

        Assert.Contains("Daily summary task removed.", cut.Markup);
        Assert.Contains("enabled: false", File.ReadAllText(ui.ConfigPath));
        Assert.Equal("unregister", host.Calls[1]);
    }

    private sealed class RecordingHost : IDailySummaryTaskHost
    {
        public bool IsWindows => true;
        public bool ExistsValue { get; set; }
        public List<string> Calls { get; } = new();
        public bool Exists() => ExistsValue;
        public void Register(string configPath, string timeOfDay)
        {
            Calls.Add($"register {timeOfDay} {configPath}");
            ExistsValue = true;
        }
        public void Unregister()
        {
            Calls.Add("unregister");
            ExistsValue = false;
        }
    }
}
