using System.CommandLine;
using System.Text.Json;
using Spectre.Console;
using VeeamVhcAws.Core.Config;
using VeeamVhcAws.Core.Models;
using VeeamVhcAws.Core.Patterns;
using VeeamVhcAws.Ui;

namespace VeeamVhcAws.Commands;

public static class DiagnoseCommand
{
    public static Command Create()
    {
        var command = new Command("diagnose", "Show known error patterns or match an error string against them");
        var configOption = CommandHelpers.ConfigOption();
        var listPatternsOption = new Option<bool>(new[] { "--list-patterns", "-l" }, "List all known error patterns");
        var matchOption = new Option<string?>(new[] { "--match", "-m" }, "Match an error string against patterns");

        command.AddOption(configOption);
        command.AddOption(listPatternsOption);
        command.AddOption(matchOption);

        command.SetHandler((string? config, bool listPatterns, string? match) =>
        {
            var configPath = ConfigLoader.GetConfigPath(config);
            var cfg = ConfigLoader.LoadConfig(configPath);
            var pe = CommandHelpers.BuildPatternEngine(cfg);

            if (pe == null)
            {
                AnsiConsole.MarkupLine("[yellow]No error patterns configured.[/]");
                Environment.ExitCode = 0;
                return;
            }

            if (listPatterns)
            {
                var table = new Table();
                table.Title = new TableTitle("Known Error Patterns");
                table.AddColumn("Category");
                table.AddColumn("Severity");
                table.AddColumn("Pattern");
                table.AddColumn("Message");
                table.AddColumn("Source");

                foreach (var pattern in pe.Patterns)
                {
                    var patternStr = pattern.Pattern.Length > 60 ? pattern.Pattern[..60] : pattern.Pattern;
                    var messageStr = pattern.Message.Length > 80 ? pattern.Message[..80] : pattern.Message;
                    var sevMarkup = $"{Theme.SevStyle(pattern.Severity)}{pattern.Severity.ToLowerString()}[/]";

                    table.AddRow(
                        pattern.Category,
                        sevMarkup,
                        Markup.Escape(patternStr),
                        Markup.Escape(messageStr),
                        pattern.Source);
                }

                AnsiConsole.Write(table);
            }

            if (match != null)
            {
                var result = pe.Classify(match);
                if (result != null)
                {
                    AnsiConsole.MarkupLine("\n[bold]Match found:[/]");
                    AnsiConsole.MarkupLine($"  Category: {result.Category}");
                    AnsiConsole.MarkupLine($"  Severity: {result.Severity.ToLowerString()}");
                    AnsiConsole.MarkupLine($"  Message:  {Markup.Escape(result.Message)}");
                    if (!string.IsNullOrEmpty(result.Source))
                        AnsiConsole.MarkupLine($"  Source:   {result.Source}");
                    if (!string.IsNullOrEmpty(result.LogHint))
                        AnsiConsole.MarkupLine($"  Log hint: {Markup.Escape(result.LogHint)}");
                    if (!string.IsNullOrEmpty(result.Remediation))
                        AnsiConsole.MarkupLine($"  Fix:      {Markup.Escape(result.Remediation)}");

                    var extracted = pe.Extract(match, result);
                    if (extracted.Count > 0)
                        AnsiConsole.MarkupLine($"  Extracted: {Markup.Escape(JsonSerializer.Serialize(extracted))}");
                }
                else
                {
                    AnsiConsole.MarkupLine("[yellow]No matching pattern found.[/]");
                }
            }
        }, configOption, listPatternsOption, matchOption);

        return command;
    }
}
