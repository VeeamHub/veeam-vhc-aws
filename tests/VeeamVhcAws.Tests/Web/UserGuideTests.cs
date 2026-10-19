using System.Text.RegularExpressions;
using Xunit;
using VeeamVhcAws.Web.Services;

namespace VeeamVhcAws.Tests.Web;

public class UserGuideTests
{
    [Fact]
    public void EmbeddedGuide_LoadsAndRenders()
    {
        var html = UserGuide.Html;
        Assert.False(string.IsNullOrWhiteSpace(html));
        Assert.Contains("<h2", html);
        Assert.Contains("Already using it with YAML? Start here", html);
    }

    [Fact]
    public void TopSections_ListEveryH2WithItsRenderedAnchor()
    {
        var sections = UserGuide.TopSections;
        Assert.Equal("Already using it with YAML? Start here", sections[0].Title);
        Assert.Contains(sections, s => s.Title == "Common questions");
        foreach (var s in sections)
            Assert.Contains($"<h2 id=\"{s.Id}\"", UserGuide.Html);
        Assert.Equal(Regex.Matches(UserGuide.Html, "<h2 ").Count, sections.Count);
    }

    [Fact]
    public void Headings_GetAnchorsMatchingChecklistLinks()
    {
        var html = UserGuide.Html;
        Assert.Contains("id=\"how-do-i-test-a-server\"", html);
        Assert.Contains("id=\"set-it-up-from-scratch\"", html);
    }

    [Fact]
    public void RawHtml_IsEscaped()
    {
        var html = UserGuide.Render("Hello\n\n<script>alert(1)</script>\n");
        Assert.DoesNotContain("<script", html);
        Assert.Contains("&lt;script&gt;", html);
    }

    [Fact]
    public void NavLabels_MatchGuidePagesSection()
    {
        var root = FindRepoRoot();
        var layout = File.ReadAllText(Path.Combine(root, "src", "VeeamVhcAws", "Web", "Components", "Layout", "MainLayout.razor"));
        var nav = Regex.Matches(layout, @"<NavLink[^>]*>([^<]+)</NavLink>")
            .Select(m => m.Groups[1].Value.Trim())
            .ToHashSet();

        var guide = File.ReadAllLines(Path.Combine(root, "docs", "USER_GUIDE.md"));
        var pages = guide
            .SkipWhile(l => l.TrimEnd() != "## Pages").Skip(1)
            .TakeWhile(l => !l.StartsWith("## "))
            .Where(l => l.StartsWith("### "))
            .Select(l => l[4..].Trim())
            .ToHashSet();

        Assert.NotEmpty(nav);
        Assert.Equal(nav.OrderBy(x => x), pages.OrderBy(x => x));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "docs", "USER_GUIDE.md")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repo root with docs/USER_GUIDE.md not found.");
    }
}
