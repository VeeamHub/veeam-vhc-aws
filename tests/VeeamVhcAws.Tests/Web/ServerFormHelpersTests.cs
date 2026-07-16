using Xunit;
using VeeamVhcAws.Web.Components.Shared;

namespace VeeamVhcAws.Tests.Web;

/// <summary>
/// Issue #13 — auto-populate the URL field from the Name in the Add Server dialog,
/// but never clobber a URL the user has manually edited.
/// </summary>
public class ServerFormHelpersTests
{
    // ISC-1: typing a Name while URL is empty auto-populates https://<name>:9419.
    [Fact]
    public void EmptyUrl_AutoPopulatesFromName()
    {
        Assert.Equal("https://prod-vbr:9419", ServerFormHelpers.SuggestUrl("prod-vbr", "", ""));
    }

    // ISC-1: while URL still equals the last auto value, a changed Name keeps the URL in sync.
    [Fact]
    public void UrlStillAuto_FollowsNameChange()
    {
        var result = ServerFormHelpers.SuggestUrl("prod-vbaws", "https://prod-vbr:9419", "https://prod-vbr:9419");
        Assert.Equal("https://prod-vbaws:9419", result);
    }

    // ISC-2: once the user has manually edited the URL, Name typing must NOT overwrite it.
    [Fact]
    public void ManuallyEditedUrl_IsNotOverwritten()
    {
        var result = ServerFormHelpers.SuggestUrl("prod-vbr", "https://custom-host:1234", "https://prod-vbr:9419");
        Assert.Equal("https://custom-host:1234", result);
    }

    // Blank name never changes the URL.
    [Fact]
    public void BlankName_LeavesUrlUnchanged()
    {
        Assert.Equal("https://existing:9419", ServerFormHelpers.SuggestUrl("", "https://existing:9419", ""));
        Assert.Equal("", ServerFormHelpers.SuggestUrl("   ", "", ""));
    }

    // Name is trimmed into the suggested URL.
    [Fact]
    public void NameIsTrimmed()
    {
        Assert.Equal("https://host:9419", ServerFormHelpers.SuggestUrl("  host  ", "", ""));
    }

    // A Name with interior spaces would compose an invalid host — don't hand the user a broken URL.
    [Fact]
    public void NameWithInteriorSpaces_DoesNotAutoFill()
    {
        Assert.Equal("", ServerFormHelpers.SuggestUrl("my server", "", ""));
    }
}
