namespace VeeamVhcAws.Ui;

/// <summary>
/// TTY detection — determines whether rich interactive terminal output is appropriate.
/// Returns false if output is redirected, environment is non-interactive, or
/// env vars suppress color (CI, NO_COLOR, TERM=dumb).
/// </summary>
public static class TerminalCapabilities
{
    private static bool _noInteractiveFlag;

    /// <summary>
    /// Set by Program.cs when --no-interactive global option is passed.
    /// </summary>
    public static void SetNoInteractive() => _noInteractiveFlag = true;

    /// <summary>
    /// Reset for unit testing — clears the static flag.
    /// </summary>
    internal static void ResetForTesting() => _noInteractiveFlag = false;

    /// <summary>
    /// Returns true only when all conditions for rich terminal output are met:
    /// - Output is not redirected
    /// - Environment.UserInteractive is true
    /// - NO_COLOR env var is not set (non-empty)
    /// - CI env var is not set (non-empty)
    /// - TERM env var is not "dumb"
    /// - --no-interactive flag has not been set
    /// </summary>
    public static bool IsInteractive
    {
        get
        {
            if (_noInteractiveFlag) return false;
            if (Console.IsOutputRedirected) return false;
            if (!Environment.UserInteractive) return false;

            var noColor = Environment.GetEnvironmentVariable("NO_COLOR");
            if (!string.IsNullOrEmpty(noColor)) return false;

            var ci = Environment.GetEnvironmentVariable("CI");
            if (!string.IsNullOrEmpty(ci)) return false;

            var term = Environment.GetEnvironmentVariable("TERM");
            if (string.Equals(term, "dumb", StringComparison.OrdinalIgnoreCase)) return false;

            return true;
        }
    }
}
