using Markdig;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace VeeamVhcAws.Web.Services;

public record GuideSection(string Id, string Title);

public static class UserGuide
{
    public const string ResourceName = "VeeamVhcAws.docs.USER_GUIDE.md";

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .DisableHtml()
        .UsePipeTables()
        .UseAutoIdentifiers()
        .Build();

    private static readonly Lazy<string> Cached = new(() => Render(LoadMarkdown()));
    private static readonly Lazy<IReadOnlyList<GuideSection>> CachedSections = new(() => Sections(LoadMarkdown()));

    public static string Html => Cached.Value;

    /// <summary>The guide's top-level (##) sections, for the Help page's "On this page" list.</summary>
    public static IReadOnlyList<GuideSection> TopSections => CachedSections.Value;

    public static string Render(string markdown) => Markdown.ToHtml(markdown, Pipeline);

    public static IReadOnlyList<GuideSection> Sections(string markdown) =>
        Markdown.Parse(markdown, Pipeline)
            .Descendants<HeadingBlock>()
            .Where(h => h.Level == 2 && h.Inline is not null)
            .Select(h => new GuideSection(h.GetAttributes().Id ?? "", InlineText(h.Inline!)))
            .Where(s => s.Id.Length > 0)
            .ToList();

    private static string InlineText(ContainerInline inline) =>
        string.Concat(inline.Descendants<LiteralInline>().Select(l => l.Content.ToString()));

    public static string LoadMarkdown()
    {
        using var stream = typeof(UserGuide).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource {ResourceName} is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
