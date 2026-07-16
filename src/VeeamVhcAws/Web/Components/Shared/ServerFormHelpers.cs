namespace VeeamVhcAws.Web.Components.Shared;

/// <summary>
/// Pure helpers for the Add/Edit Server form. Extracted from the Razor component so the
/// auto-populate behavior (issue #13) is unit-testable without a Blazor render harness.
/// </summary>
public static class ServerFormHelpers
{
    /// <summary>Default management port used for the auto-suggested URL (VBR and VBAWS both 9419).</summary>
    public const int DefaultPort = 9419;

    /// <summary>
    /// Suggests the URL value as the user types the server Name in the Add dialog.
    /// Auto-populates <c>https://{name}:9419</c> ONLY while the URL is still empty or still equals
    /// the last auto-generated value (i.e. the user has not manually edited it). Once the user edits
    /// the URL, it no longer matches <paramref name="lastAutoUrl"/> and is never overwritten again.
    /// Returns the URL unchanged when the Name is blank.
    /// </summary>
    /// <param name="name">Current Name field value.</param>
    /// <param name="url">Current URL field value.</param>
    /// <param name="lastAutoUrl">The URL this helper last auto-generated (empty if never).</param>
    public static string SuggestUrl(string? name, string? url, string? lastAutoUrl)
    {
        name ??= "";
        url ??= "";
        lastAutoUrl ??= "";

        if (string.IsNullOrWhiteSpace(name))
            return url;

        if (url.Length == 0 || url == lastAutoUrl)
            return $"https://{name.Trim()}:{DefaultPort}";

        return url;
    }
}
