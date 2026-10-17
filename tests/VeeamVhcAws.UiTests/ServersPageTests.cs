using Bunit;
using VeeamVhcAws.Web.Components.Pages;

namespace VeeamVhcAws.UiTests;

public class ServersPageTests
{
    [Fact]
    public void EmptyPage_ShowsOneAddButton()
    {
        using var ui = new UiHarness();
        var cut = ui.Context.RenderComponent<Servers>();

        Assert.Equal("+ Add Your First Server", cut.FindAll("button").Single().TextContent.Trim());
        Assert.Contains("No servers configured yet.", cut.Markup);
    }

    [Fact]
    public void AddServer_NamesEveryControl_DefaultsVerifySslOn_AndSaves()
    {
        using var ui = new UiHarness();
        var cut = ui.Context.RenderComponent<Servers>();
        UiHarness.Click(cut, "+ Add Your First Server");

        UiHarness.AssertEveryControlHasName(cut);
        UiHarness.AssertNamed(cut, "server-name", "Name (unique label)");
        UiHarness.AssertNamed(cut, "server-url", "URL");
        UiHarness.AssertNamed(cut, "server-username", "Username");
        UiHarness.AssertNamed(cut, "server-password", "Password");
        UiHarness.AssertNamed(cut, "server-api-version", "API Version");
        Assert.Equal("password", cut.Find("#server-password").GetAttribute("type"));
        Assert.Contains("Verify SSL certificate", cut.Find("input[type=checkbox]").ParentElement!.TextContent);
        Assert.True(cut.Find("input[type=checkbox]").HasAttribute("checked"));

        cut.Find("#server-name").Input("lab-vbr");
        Assert.Equal("https://lab-vbr:9419", cut.Find("#server-url").GetAttribute("value"));
        cut.Find("#server-url").Input("https://lab-vbr.example:9419");
        cut.Find("#server-username").Input("backupadmin");
        cut.Find("#server-password").Input("verify-secret");
        UiHarness.Click(cut, "Save");

        Assert.Contains("Server 'lab-vbr' added", cut.Markup);
        Assert.Contains("lab-vbr", cut.Markup);
        Assert.Contains(">yes<", cut.Markup);
        Assert.DoesNotContain("+ Add Your First Server", cut.Markup);
        Assert.Contains("+ Add Server", cut.Markup);

        var yaml = File.ReadAllText(ui.ConfigPath);
        Assert.Contains("name: lab-vbr", yaml);
        Assert.Contains("url: https://lab-vbr.example:9419", yaml);
        Assert.Contains("username: backupadmin", yaml);
        Assert.Contains("verify_ssl: true", yaml);
        Assert.Contains("ENC:", yaml);
        Assert.DoesNotContain("verify-secret", yaml);

        var again = ui.Context.RenderComponent<Servers>();
        Assert.Contains("lab-vbr", again.Markup);
        Assert.DoesNotContain("No servers configured yet.", again.Markup);
    }

    [Fact]
    public void Cancel_DiscardsTheDraft_AndEscapeClosesTheDialog()
    {
        using var ui = new UiHarness();
        var cut = ui.Context.RenderComponent<Servers>();
        UiHarness.Click(cut, "+ Add Your First Server");
        cut.Find("#server-name").Input("discard-me");
        cut.Find(".modal").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Escape" });

        Assert.DoesNotContain("Add Server", cut.FindAll("h2").Select(h => h.TextContent));
        Assert.DoesNotContain("discard-me", cut.Markup);
        Assert.DoesNotContain("discard-me", File.ReadAllText(ui.ConfigPath));
    }

    [Fact]
    public void Delete_UsesDistinctNames_AndBackdropDoesNotCancel()
    {
        using var ui = new UiHarness();
        var cut = ui.Context.RenderComponent<Servers>();
        AddLab(cut);

        UiHarness.Click(cut, "Delete lab-vbr");
        Assert.Equal("Delete server?", cut.Find("h2").TextContent);
        Assert.Equal("Confirm delete", cut.FindAll("button").Single(b => b.TextContent.Trim() == "Confirm delete").TextContent.Trim());
        cut.Find(".modal-overlay").Click();
        Assert.Equal("Delete server?", cut.Find("h2").TextContent);
        Assert.Contains("lab-vbr", File.ReadAllText(ui.ConfigPath));

        cut.Find(".modal").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Escape" });
        Assert.Empty(cut.FindAll("h2"));
        Assert.Contains("lab-vbr", File.ReadAllText(ui.ConfigPath));

        UiHarness.Click(cut, "Delete lab-vbr");
        UiHarness.Click(cut, "Cancel");
        Assert.Contains("lab-vbr", cut.Markup);

        UiHarness.Click(cut, "Delete lab-vbr");
        UiHarness.Click(cut, "Confirm delete");
        Assert.Contains("Server 'lab-vbr' deleted", cut.Markup);
        Assert.Contains("No servers configured yet.", cut.Markup);
        Assert.DoesNotContain("name: lab-vbr", File.ReadAllText(ui.ConfigPath));
    }

    [Fact]
    public void EditCancel_KeepsTheSavedUrl()
    {
        using var ui = new UiHarness();
        var cut = ui.Context.RenderComponent<Servers>();
        AddLab(cut);
        UiHarness.Click(cut, "Edit lab-vbr");
        Assert.Equal("********", cut.Find("#server-password").GetAttribute("value"));
        cut.Find("#server-url").Input("https://replaced.example:9419");
        UiHarness.Click(cut, "Cancel");

        Assert.Contains("https://lab-vbr.example:9419", cut.Markup);
        Assert.DoesNotContain("replaced.example", File.ReadAllText(ui.ConfigPath));
    }

    [Fact]
    public void EditUrl_WithoutANewPassword_IsRejected()
    {
        using var ui = new UiHarness();
        var cut = ui.Context.RenderComponent<Servers>();
        AddLab(cut);
        UiHarness.Click(cut, "Edit lab-vbr");
        cut.Find("#server-url").Input("https://lab-vbr.example:9420");
        UiHarness.Click(cut, "Save");

        Assert.Contains("A server URL change requires a new password.", cut.Markup);
        var yaml = File.ReadAllText(ui.ConfigPath);
        Assert.Contains("url: https://lab-vbr.example:9419", yaml);
        Assert.DoesNotContain("9420", yaml);
    }

    private static void AddLab(IRenderedFragment cut)
    {
        UiHarness.Click(cut, "+ Add Your First Server");
        cut.Find("#server-name").Input("lab-vbr");
        cut.Find("#server-url").Input("https://lab-vbr.example:9419");
        cut.Find("#server-username").Input("backupadmin");
        cut.Find("#server-password").Input("verify-secret");
        UiHarness.Click(cut, "Save");
    }
}
