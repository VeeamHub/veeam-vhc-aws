using VeeamVhcAws.Core.Models;

namespace VeeamVhcAws.Ui;

/// <summary>
/// Severity-to-style mapping for Spectre.Console markup and ASCII fallback.
/// </summary>
public static class Theme
{
    // All ASCII prefixes must be the same length for column alignment.
    // Longest is "[CRIT]" = 6 chars + 1 space = 7. We use 7 chars for all.
    private const string PrefixOk   = "[OK]   "; // 7 chars
    private const string PrefixWarn = "[WARN] "; // 7 chars
    private const string PrefixCrit = "[CRIT] "; // 7 chars
    private const string PrefixErr  = "[ERR]  "; // 7 chars

    /// <summary>
    /// Returns a Spectre.Console markup color style tag for the given severity.
    /// Example: "[green]", "[yellow]", "[red bold]"
    /// </summary>
    public static string SevStyle(Severity severity) => severity switch
    {
        Severity.Ok       => "[green]",
        Severity.Warning  => "[yellow]",
        Severity.Critical => "[red bold]",
        Severity.Error    => "[red bold]",
        _                 => "[white]",
    };

    /// <summary>
    /// Returns a short ASCII icon for the given severity.
    /// Used in non-interactive mode and for compact display.
    /// </summary>
    public static string Icon(Severity severity) => severity switch
    {
        Severity.Ok       => "[OK]",
        Severity.Warning  => "[!]",
        Severity.Critical => "[!!]",
        Severity.Error    => "[X]",
        _                 => "[?]",
    };

    /// <summary>
    /// Returns a fixed-width ASCII prefix for aligned console output.
    /// All prefixes are the same length for column alignment.
    /// </summary>
    public static string AsciiPrefix(Severity severity) => severity switch
    {
        Severity.Ok       => PrefixOk,
        Severity.Warning  => PrefixWarn,
        Severity.Critical => PrefixCrit,
        Severity.Error    => PrefixErr,
        _                 => "[???]  ",
    };

    public const string Close = "[/]";

    public static string Colorize(Severity severity, string text)
    {
        if (!TerminalCapabilities.IsInteractive)
            return $"{AsciiPrefix(severity)}{text}";

        return $"{SevStyle(severity)}{text}{Close}";
    }
}
