using Bunit;
using OutputsPage = VeeamVhcAws.Web.Components.Pages.Outputs;

namespace VeeamVhcAws.UiTests;

public class OutputsPageTests
{
    [Fact]
    public void EmptyPage_ShowsOneAddButton()
    {
        using var ui = new UiHarness(writeConfig: false);
        var cut = ui.Context.RenderComponent<OutputsPage>();

        Assert.Equal("+ Add Your First Handler", cut.FindAll("button").Single().TextContent.Trim());
        Assert.Contains("No output handlers configured.", cut.Markup);
    }

    [Fact]
    public void NewEmailHandler_NamesThePassword_AndStartsBlank()
    {
        using var ui = new UiHarness(writeConfig: false);
        var cut = ui.Context.RenderComponent<OutputsPage>();
        UiHarness.Click(cut, "+ Add Your First Handler");
        cut.Find("#output-type").Change("email");

        UiHarness.AssertEveryControlHasName(cut);
        UiHarness.AssertNamed(cut, "output-smtp-password", "SMTP password");
        Assert.Equal("password", cut.Find("#output-smtp-password").GetAttribute("type"));
        Assert.Equal("", cut.Find("#output-smtp-password").GetAttribute("value"));
    }

    [Fact]
    public void BlankWebhook_ShowsTheUrlError_AndCancelWritesNothing()
    {
        using var ui = new UiHarness(writeConfig: false);
        var cut = ui.Context.RenderComponent<OutputsPage>();
        UiHarness.Click(cut, "+ Add Your First Handler");
        UiHarness.Click(cut, "Save");
        Assert.Contains("Webhook URL is required", cut.Markup);

        cut.Find("#output-url").Input("https://example.invalid/hook");
        UiHarness.Click(cut, "Cancel");
        Assert.Contains("No output handlers configured.", cut.Markup);
        Assert.False(File.Exists(ui.ConfigPath));
    }

    [Fact]
    public void JsonFile_Saves_AndDeleteUsesADistinctName()
    {
        using var ui = new UiHarness(writeConfig: false);
        var cut = ui.Context.RenderComponent<OutputsPage>();
        UiHarness.Click(cut, "+ Add Your First Handler");
        cut.Find("#output-type").Change("json_file");
        UiHarness.AssertNamed(cut, "output-path", "File path");
        cut.Find("#output-path").Input("./veeam-vhc-aws-output.json");
        UiHarness.Click(cut, "Save");

        Assert.Contains("Output handler added", cut.Markup);
        Assert.Contains("json_file", cut.Markup);
        var yaml = File.ReadAllText(ui.ConfigPath);
        Assert.Contains("json_file", yaml);
        Assert.Contains("veeam-vhc-aws-output.json", yaml);

        UiHarness.Click(cut, "Delete json_file 1");
        cut.Find(".modal-overlay").Click();
        Assert.Equal("Delete output handler?", cut.Find("h2").TextContent);
        UiHarness.Click(cut, "Confirm delete");
        Assert.Contains("Output handler deleted", cut.Markup);
        Assert.Contains("No output handlers configured.", cut.Markup);
    }
}
