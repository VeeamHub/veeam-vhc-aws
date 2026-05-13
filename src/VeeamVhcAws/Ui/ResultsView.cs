using Spectre.Console;
using VeeamVhcAws.Core.Models;

namespace VeeamVhcAws.Ui;

/// <summary>
/// Renders post-run results table grouped by severity.
/// Only renders when TerminalCapabilities.IsInteractive.
/// When json_stdout is in outputs, output goes to stderr to avoid polluting JSON.
/// </summary>
public static class ResultsView
{
    /// <summary>
    /// Render monitor results to the terminal.
    /// Groups findings by severity: Error -> Critical -> Warning -> Ok.
    /// </summary>
    public static void Render(IEnumerable<MonitorResult> results, bool useStderr = false)
    {
        var resultList = results.ToList();

        var allFindings = resultList.SelectMany(r => r.Findings).ToList();
        var critCount = allFindings.Count(f => f.Severity == Severity.Critical);
        var warnCount = allFindings.Count(f => f.Severity == Severity.Warning);
        var errCount = allFindings.Count(f => f.Severity == Severity.Error);
        var serverCount = resultList.Select(r => r.Server).Where(s => !string.IsNullOrEmpty(s)).Distinct().Count();

        if (!TerminalCapabilities.IsInteractive)
        {
            RenderAscii(allFindings, critCount, warnCount, errCount, serverCount, useStderr);
            return;
        }

        AnsiConsole.WriteLine();
        if (errCount == 0 && critCount == 0 && warnCount == 0)
        {
            AnsiConsole.MarkupLine($"[green][OK] All monitors passed — {serverCount} server(s), 0 findings[/]");
        }
        else
        {
            var parts = new List<string>();
            if (errCount > 0) parts.Add($"{errCount} error");
            if (critCount > 0) parts.Add($"{critCount} critical");
            if (warnCount > 0) parts.Add($"{warnCount} warning");
            AnsiConsole.MarkupLine($"[red bold][!!] {string.Join(", ", parts)} — action required[/]");
        }

        if (allFindings.Count == 0)
        {
            AnsiConsole.WriteLine();
            return;
        }

        // Build findings table ordered by severity descending
        var table = new Table();
        table.AddColumn("[bold]Severity[/]");
        table.AddColumn("[bold]Resource[/]");
        table.AddColumn("[bold]Message[/]");
        table.Border(TableBorder.Rounded);
        table.Expand();

        var ordered = allFindings
            .OrderByDescending(f => f.Severity.Rank())
            .ToList();

        foreach (var finding in ordered)
        {
            table.AddRow(
                $"{Theme.SevStyle(finding.Severity)}{Theme.Icon(finding.Severity)} {finding.Severity}{Theme.Close}",
                finding.Resource.EscapeMarkup(),
                finding.Message.EscapeMarkup()
            );
        }

        AnsiConsole.WriteLine();
        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    private static void RenderAscii(List<Finding> allFindings, int critCount, int warnCount, int errCount, int serverCount, bool useStderr)
    {
        var writer = useStderr ? Console.Error : Console.Out;

        writer.WriteLine();
        if (errCount == 0 && critCount == 0 && warnCount == 0)
        {
            writer.WriteLine($"[OK]    All monitors passed -- {serverCount} server(s), 0 findings");
        }
        else
        {
            var parts = new List<string>();
            if (errCount > 0) parts.Add($"{errCount} error");
            if (critCount > 0) parts.Add($"{critCount} critical");
            if (warnCount > 0) parts.Add($"{warnCount} warning");
            writer.WriteLine($"[!!]    {string.Join(", ", parts)} -- action required");
        }

        if (allFindings.Count == 0)
        {
            writer.WriteLine();
            return;
        }

        writer.WriteLine();
        writer.WriteLine($"{"Severity",-10} {"Resource",-40} Message");
        writer.WriteLine(new string('-', 100));

        var ordered = allFindings.OrderByDescending(f => f.Severity.Rank());
        foreach (var finding in ordered)
        {
            var prefix = Theme.AsciiPrefix(finding.Severity);
            var resource = finding.Resource.Length > 40 ? finding.Resource[..37] + "..." : finding.Resource.PadRight(40);
            writer.WriteLine($"{prefix} {resource} {finding.Message}");
        }

        writer.WriteLine();
    }
}
