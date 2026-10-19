using System.Text.Json;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using VeeamVhcAws.Scheduling;
using VeeamVhcAws.Web;
using VeeamVhcAws.Web.Services;

namespace VeeamVhcAws.UiTests;

sealed class UiHarness : IDisposable
{
    public string Dir { get; }
    public string ConfigPath { get; }
    public string StatePath { get; }
    public ConfigService Config { get; }
    public StateService State { get; }
    public BunitContext Context { get; }

    public UiHarness(string? stateJson = null, bool writeConfig = true, IDailySummaryTaskHost? tasks = null)
    {
        Dir = Path.Combine(Path.GetTempPath(), "vhc-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Dir);
        ConfigPath = Path.Combine(Dir, "veeam-vhc-aws.yaml");
        StatePath = Path.Combine(Dir, "state.json");
        if (writeConfig)
            File.WriteAllText(ConfigPath, "global:\n  state_file: " + StatePath + "\n");
        if (stateJson is not null)
            File.WriteAllText(StatePath, stateJson);

        var options = new WebHostOptions(ConfigPath, "127.0.0.1", 0);
        Config = new ConfigService(options);
        State = new StateService(options);
        Context = new BunitContext();
        Context.JSInterop.Mode = JSRuntimeMode.Loose;
        Context.Services.AddSingleton(options);
        Context.Services.AddSingleton(Config);
        Context.Services.AddSingleton(State);
        Context.Services.AddSingleton(tasks ?? new NoopDailySummaryTaskHost());
    }

    public void Dispose()
    {
        Context.Dispose();
        Directory.Delete(Dir, recursive: true);
    }

    public static void Click(IRenderedComponent<IComponent> cut, string name)
    {
        var matches = cut.FindAll("button").Where(b => b.TextContent.Trim() == name).ToList();
        Assert.Single(matches);
        matches[0].Click();
    }

    public static string LabelText(IRenderedComponent<IComponent> cut, string id)
    {
        var label = cut.Find($"label[for='{id}']");
        return string.Join(" ", label.TextContent.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    public static void AssertNamed(IRenderedComponent<IComponent> cut, string id, string expectedLabel)
    {
        var control = cut.Find("#" + id);
        Assert.Equal(id, control.Id);
        Assert.Equal(expectedLabel, LabelText(cut, id));
    }

    public static void AssertEveryControlHasName(IRenderedComponent<IComponent> cut)
    {
        var unnamed = new List<string>();
        foreach (var control in cut.FindAll("input, select, textarea"))
        {
            var type = control.GetAttribute("type");
            if (type is "hidden" or "submit")
                continue;
            var wrapped = string.Equals(control.ParentElement?.TagName, "LABEL", StringComparison.OrdinalIgnoreCase);
            var id = control.Id;
            var labeled = !string.IsNullOrEmpty(id) && cut.FindAll($"label[for='{id}']").Count == 1;
            if (!wrapped && !labeled)
                unnamed.Add(control.OuterHtml);
        }

        Assert.Equal("", string.Join("\n", unnamed));
    }

    public static bool HasSuppression(string statePath, string key)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(statePath));
        return doc.RootElement.TryGetProperty("suppressions", out var suppressions)
            && suppressions.TryGetProperty(key, out _);
    }
}
