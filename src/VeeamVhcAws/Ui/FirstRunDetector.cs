using Spectre.Console;

namespace VeeamVhcAws.Ui;

/// <summary>
/// Detects whether the setup wizard should be prompted on first run.
/// Checks if config file is missing and terminal is interactive.
/// </summary>
public static class FirstRunDetector
{
    /// <summary>
    /// Returns true if the config file is missing AND the terminal is interactive.
    /// </summary>
    public static bool ShouldPrompt(string configPath)
    {
        if (!TerminalCapabilities.IsInteractive) return false;
        return !File.Exists(configPath);
    }

    /// <summary>
    /// If config is missing and terminal is interactive, prompts the user to run setup.
    /// Returns true if the user confirmed (setup should run).
    /// Returns false if the user declined or conditions not met.
    /// </summary>
    public static bool CheckAndPrompt(string configPath)
    {
        if (!ShouldPrompt(configPath)) return false;

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[yellow]No configuration file found at:[/] {configPath.EscapeMarkup()}");
        AnsiConsole.WriteLine();

        return AnsiConsole.Confirm("Run the setup wizard to create one now?", false);
    }
}
