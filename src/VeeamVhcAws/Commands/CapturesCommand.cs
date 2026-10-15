using System.CommandLine;
using System.Text.Json;
using Spectre.Console;
using VeeamVhcAws.Core.Config;
using VeeamVhcAws.Core.Models;
using VeeamVhcAws.Core.State;
using VeeamVhcAws.Ui;

namespace VeeamVhcAws.Commands;

public static class CapturesCommand
{
    public static Command Create()
    {
        var command = new Command("captures", "View and manage captured errors and suppressions");
        var configOption = CommandHelpers.ConfigOption();

        var listCommand = CreateListCommand(configOption);
        var suppressCommand = CreateSuppressCommand(configOption);
        var unsuppressCommand = CreateUnsuppressCommand(configOption);

        command.AddCommand(listCommand);
        command.AddCommand(suppressCommand);
        command.AddCommand(unsuppressCommand);

        return command;
    }

    private static Command CreateListCommand(Option<string?> configOption)
    {
        var command = new Command("list", "List captured errors");
        var allOption = new Option<bool>("--all", "Show all captured errors including suppressed");
        command.AddOption(configOption);
        command.AddOption(allOption);

        command.SetHandler((string? config, bool showAll) =>
        {
            var state = LoadState(config);
            if (state == null) return;

            var capturedErrors = GetStateSection(state, "captured_errors");
            var suppressions = GetStateSection(state, "suppressions");
            if (capturedErrors.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No captured errors.[/]");
                return;
            }

            // Sort by last_seen descending
            var entries = capturedErrors
                .Select(kv => (Key: kv.Key, Entry: kv.Value as Dictionary<string, object>))
                .Where(x => x.Entry != null)
                .Select(x => (x.Key, x.Entry, Suppressed: Suppression.IsSuppressed(x.Key, x.Entry, suppressions)))
                .OrderByDescending(x => x.Entry!.GetValueOrDefault("last_seen", "")?.ToString() ?? "")
                .ToList();

            if (!showAll)
                entries = entries.Where(x => !x.Suppressed).ToList();

            if (entries.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No unsuppressed captured errors. Use --all to include suppressed.[/]");
                return;
            }

            var table = new Table();
            table.Title = new TableTitle("Captured Errors");
            table.AddColumn("Key");
            table.AddColumn("Severity");
            table.AddColumn("Resource");
            table.AddColumn("Text");
            table.AddColumn("Count");
            table.AddColumn("Suppressed");

            foreach (var (key, entry, suppressed) in entries)
            {
                var e = entry!;
                var keyShort = key.Length > 8 ? key[..8] : key;
                var severity = e.GetValueOrDefault("severity", "")?.ToString() ?? "";
                var resource = e.GetValueOrDefault("resource", "")?.ToString() ?? "";
                var text = e.GetValueOrDefault("text", "")?.ToString() ?? "";
                var count = e.GetValueOrDefault("count", 0);

                if (resource.Length > 40) resource = resource[..40];
                if (text.Length > 60) text = text[..60];

                var sev = SeverityExtensions.ParseSeverity(severity);
                var sevMarkup = $"{Theme.SevStyle(sev)}{Markup.Escape(severity)}[/]";
                var countStr = count is double d ? ((int)d).ToString() : count?.ToString() ?? "0";

                table.AddRow(
                    keyShort,
                    sevMarkup,
                    Markup.Escape(resource),
                    Markup.Escape(text),
                    countStr,
                    suppressed ? "[red]yes[/]" : "no");
            }

            AnsiConsole.Write(table);
        }, configOption, allOption);

        return command;
    }

    private static Command CreateSuppressCommand(Option<string?> configOption)
    {
        var command = new Command("suppress", "Suppress a captured error by key prefix");
        var keyArg = new Argument<string>("key", "Key prefix to suppress (first 8+ chars)");
        command.AddArgument(keyArg);
        command.AddOption(configOption);

        command.SetHandler((string keyPrefix, string? config) =>
        {
            var statePath = GetStatePath(config);
            lock (StateFile.Gate(statePath))
            {
                using var hold = StateFile.Acquire(statePath);
                SuppressCaptured(keyPrefix, config);
            }
        }, keyArg, configOption);

        return command;
    }

    private static void SuppressCaptured(string keyPrefix, string? config)
    {
        var (statePath, state) = LoadStateWithPath(config);
        if (state == null) return;

        // Search active findings too: a key can exist there without a captured_errors entry
        // (for example in a state file written before captured_errors existed). The web
        // Alerts page already accepts both.
        var capturedErrors = GetStateSection(state, "captured_errors");
        var findings = GetStateSection(state, "findings");
        var matches = capturedErrors.Keys.Union(findings.Keys)
            .Where(k => k.StartsWith(keyPrefix, StringComparison.OrdinalIgnoreCase)).ToList();

        if (matches.Count == 0)
        {
            AnsiConsole.MarkupLine($"[red]No captured error matches key prefix '{Markup.Escape(keyPrefix)}'.[/]");
            Environment.ExitCode = 1;
            return;
        }

        if (matches.Count > 1)
        {
            AnsiConsole.MarkupLine($"[red]Ambiguous key prefix '{Markup.Escape(keyPrefix)}' — matches {matches.Count} entries:[/]");
            foreach (var m in matches)
            {
                var entry = (capturedErrors.GetValueOrDefault(m) ?? findings.GetValueOrDefault(m)) as Dictionary<string, object>;
                var text = (entry?.GetValueOrDefault("text") ?? entry?.GetValueOrDefault("message"))?.ToString() ?? "";
                AnsiConsole.MarkupLine($"  {m[..8]}  {Markup.Escape(text)}");
            }
            Environment.ExitCode = 1;
            return;
        }

        var matchedKey = matches[0];
        var suppressions = GetStateSection(state, "suppressions");
        suppressions[matchedKey] = new Dictionary<string, object>
        {
            ["suppressed_at"] = DateTime.UtcNow.ToString("O")
        };
        state["suppressions"] = suppressions;
        if (Suppression.MarkCaptured(capturedErrors, matchedKey, suppressed: true))
            state["captured_errors"] = capturedErrors;

        SaveState(statePath!, state);
        AnsiConsole.MarkupLine($"[green]Suppressed:[/] {matchedKey[..8]}");
    }

    private static Command CreateUnsuppressCommand(Option<string?> configOption)
    {
        var command = new Command("unsuppress", "Remove suppression for a captured error by key prefix");
        var keyArg = new Argument<string>("key", "Key prefix to unsuppress (first 8+ chars)");
        command.AddArgument(keyArg);
        command.AddOption(configOption);

        command.SetHandler((string keyPrefix, string? config) =>
        {
            var statePath = GetStatePath(config);
            lock (StateFile.Gate(statePath))
            {
                using var hold = StateFile.Acquire(statePath);
                UnsuppressCaptured(keyPrefix, config);
            }
        }, keyArg, configOption);

        return command;
    }

    private static void UnsuppressCaptured(string keyPrefix, string? config)
    {
        var (statePath, state) = LoadStateWithPath(config);
        if (state == null) return;

        // Same rule as list and the web UI: an entry is suppressed when it is in the
        // suppressions map or its captured_errors flag is set, so either one can match.
        var suppressions = GetStateSection(state, "suppressions");
        var capturedErrors = GetStateSection(state, "captured_errors");
        var matches = suppressions.Keys
            .Union(capturedErrors.Where(kv => Suppression.IsSuppressed(kv.Key, kv.Value as Dictionary<string, object>, suppressions)).Select(kv => kv.Key))
            .Where(k => k.StartsWith(keyPrefix, StringComparison.OrdinalIgnoreCase)).ToList();

        if (matches.Count == 0)
        {
            AnsiConsole.MarkupLine($"[red]No suppression matches key prefix '{Markup.Escape(keyPrefix)}'.[/]");
            Environment.ExitCode = 1;
            return;
        }

        if (matches.Count > 1)
        {
            AnsiConsole.MarkupLine($"[red]Ambiguous key prefix '{Markup.Escape(keyPrefix)}' — matches {matches.Count} suppressions:[/]");
            foreach (var m in matches)
                AnsiConsole.MarkupLine($"  {m[..8]}");
            Environment.ExitCode = 1;
            return;
        }

        var matchedKey = matches[0];
        suppressions.Remove(matchedKey);
        state["suppressions"] = suppressions;
        if (Suppression.MarkCaptured(capturedErrors, matchedKey, suppressed: false))
            state["captured_errors"] = capturedErrors;

        SaveState(statePath!, state);
        AnsiConsole.MarkupLine($"[green]Unsuppressed:[/] {matchedKey[..8]}");
    }

    private static string GetStatePath(string? configArg)
    {
        var configPath = ConfigLoader.GetConfigPath(configArg);
        return ConfigLoader.ResolveStateFile(configPath);
    }

    private static Dictionary<string, object>? LoadState(string? configArg)
    {
        var statePath = GetStatePath(configArg);
        if (!File.Exists(statePath))
        {
            AnsiConsole.MarkupLine($"[yellow]State file not found: {Markup.Escape(statePath)}[/]");
            AnsiConsole.MarkupLine("[dim]Run a monitor first to generate state.[/]");
            return null;
        }

        try
        {
            var json = File.ReadAllText(statePath);
            var doc = JsonDocument.Parse(json);
            return DeserializeState(doc.RootElement);
        }
        catch (Exception e)
        {
            AnsiConsole.MarkupLine($"[red]Could not load state file: {Markup.Escape(e.Message)}[/]");
            return null;
        }
    }

    private static (string? Path, Dictionary<string, object>? State) LoadStateWithPath(string? configArg)
    {
        var statePath = GetStatePath(configArg);
        if (!File.Exists(statePath))
        {
            AnsiConsole.MarkupLine($"[yellow]State file not found: {Markup.Escape(statePath)}[/]");
            AnsiConsole.MarkupLine("[dim]Run a monitor first to generate state.[/]");
            return (null, null);
        }

        try
        {
            var json = File.ReadAllText(statePath);
            var doc = JsonDocument.Parse(json);
            return (statePath, DeserializeState(doc.RootElement));
        }
        catch (Exception e)
        {
            AnsiConsole.MarkupLine($"[red]Could not load state file: {Markup.Escape(e.Message)}[/]");
            return (null, null);
        }
    }

    private static Dictionary<string, object> GetStateSection(Dictionary<string, object> state, string key)
    {
        if (state.TryGetValue(key, out var val) && val is Dictionary<string, object> section)
            return section;
        return new Dictionary<string, object>();
    }

    private static Dictionary<string, object> DeserializeState(JsonElement element)
    {
        var result = new Dictionary<string, object>();
        foreach (var prop in element.EnumerateObject())
        {
            result[prop.Name] = prop.Value.ValueKind switch
            {
                JsonValueKind.Object => DeserializeState(prop.Value),
                JsonValueKind.String => prop.Value.GetString()!,
                JsonValueKind.Number => prop.Value.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => prop.Value.ToString()
            };
        }
        return result;
    }

    private static void SaveState(string path, Dictionary<string, object> state)
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        var json = JsonSerializer.Serialize(state, options);
        StateFile.WriteAtomic(path, json);
    }
}
