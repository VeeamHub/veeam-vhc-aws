using Spectre.Console;

namespace VeeamVhcAws.Ui;

/// <summary>
/// Multi-step interactive setup wizard.
/// Collects server configuration, notification channels, output options,
/// and thresholds, then writes a config file via ConfigBuilder.
/// Only invoked in interactive mode — falls back to template copy in non-interactive.
/// </summary>
public static class SetupWizard
{
    /// <summary>
    /// Run the full 7-step wizard and write config to outputPath.
    /// </summary>
    public static void Run(string outputPath)
    {
        if (!TerminalCapabilities.IsInteractive)
            throw new InvalidOperationException("SetupWizard requires an interactive terminal. Use 'setup --template' in non-interactive mode.");

        var answers = new WizardAnswers();

        // Step 1: Welcome banner
        RunStep1_Welcome();

        // Step 2: Server collection
        answers.Servers = RunStep2_Servers();

        // Step 3: Optional test connection
        RunStep3_TestConnection(answers.Servers, outputPath);

        // Step 4: Notification channels
        RunStep4_Notifications(answers);

        // Step 5: Output options
        RunStep5_Outputs(answers);

        // Step 6: Thresholds
        RunStep6_Thresholds(answers);

        // Step 7: Review and write
        RunStep7_Review(answers, outputPath);
    }

    private static void RunStep1_Welcome()
    {
        AnsiConsole.Clear();
        AnsiConsole.Write(new FigletText("VHC Setup").Color(Color.Green));
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Welcome to the Veeam VHC AWS setup wizard.[/]");
        AnsiConsole.MarkupLine("This wizard will create a configuration file for monitoring your Veeam infrastructure.");
        AnsiConsole.WriteLine();
        AnsiConsole.Confirm("Press Enter to begin...", true);
        AnsiConsole.WriteLine();
    }

    private static List<WizardServer> RunStep2_Servers()
    {
        AnsiConsole.Write(new Rule("[bold]Step 2 of 7: Configure Servers[/]"));
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[grey]Add each Veeam server you want to monitor.[/]");
        AnsiConsole.WriteLine();

        var servers = new List<WizardServer>();

        do
        {
            AnsiConsole.MarkupLine($"[bold]Server {servers.Count + 1}[/]");

            var name = AnsiConsole.Ask<string>("Server name (unique label, e.g. prod-vbr):");
            name = name.Trim();

            var type = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("Server type:")
                    .AddChoices("vbr", "vbaws"));

            var url = AnsiConsole.Ask<string>("Server URL (e.g. https://vbr-server:9419):");
            url = url.Trim();

            var username = AnsiConsole.Ask<string>("Username:");
            username = username.Trim();

            var password = AnsiConsole.Prompt(
                new TextPrompt<string>("Password:")
                    .Secret());

            var verifySsl = AnsiConsole.Confirm("Verify SSL certificate?", false);

            servers.Add(new WizardServer
            {
                Name = name,
                Type = type,
                Url = url,
                Username = username,
                Password = password,
                VerifySsl = verifySsl,
            });

            AnsiConsole.MarkupLine($"[green]Server '{name}' added.[/]");
            AnsiConsole.WriteLine();

        } while (AnsiConsole.Confirm("Add another server?", false));

        return servers;
    }

    private static void RunStep3_TestConnection(List<WizardServer> servers, string outputPath)
    {
        AnsiConsole.Write(new Rule("[bold]Step 3 of 7: Test Connections (Optional)[/]"));
        AnsiConsole.WriteLine();

        if (!AnsiConsole.Confirm("Test connections to your servers now?", false))
        {
            AnsiConsole.MarkupLine("[grey]Skipping connection test. You can run 'veeam-vhc-aws test-connection' later.[/]");
            AnsiConsole.WriteLine();
            return;
        }

        AnsiConsole.MarkupLine("[grey]Note: Connections will be tested after config is written.[/]");
        AnsiConsole.WriteLine();
    }

    private static void RunStep4_Notifications(WizardAnswers answers)
    {
        AnsiConsole.Write(new Rule("[bold]Step 4 of 7: Notification Channels[/]"));
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[grey]Select notification channels (Space to select, Enter to confirm).[/]");
        AnsiConsole.WriteLine();

        var channels = AnsiConsole.Prompt(
            new MultiSelectionPrompt<string>()
                .Title("Notification channels:")
                .NotRequired()
                .AddChoices("Slack", "Teams", "Email", "ntfy", "None"));

        if (channels.Contains("Slack"))
        {
            answers.IncludeSlack = true;
            answers.SlackWebhookUrl = AnsiConsole.Ask<string>("Slack webhook URL:");
        }

        if (channels.Contains("Teams"))
        {
            answers.IncludeTeams = true;
            answers.TeamsWebhookUrl = AnsiConsole.Ask<string>("Teams webhook URL:");
        }

        if (channels.Contains("Email"))
        {
            answers.IncludeEmail = true;
            answers.EmailSmtpHost = AnsiConsole.Ask<string>("SMTP host:");
            answers.EmailSmtpPort = AnsiConsole.Ask("SMTP port:", 587);
            answers.EmailFrom = AnsiConsole.Ask<string>("From address:");
            answers.EmailTo = AnsiConsole.Ask<string>("To address:");

            if (AnsiConsole.Confirm("SMTP authentication?", false))
            {
                answers.EmailSmtpUsername = AnsiConsole.Ask<string>("SMTP username:");
                answers.EmailSmtpPassword = AnsiConsole.Prompt(
                    new TextPrompt<string>("SMTP password:")
                        .Secret());
            }
        }

        if (channels.Contains("ntfy"))
        {
            answers.IncludeNtfy = true;
            answers.NtfyUrl = AnsiConsole.Ask<string>("ntfy topic URL:");
        }

        AnsiConsole.WriteLine();
    }

    private static void RunStep5_Outputs(WizardAnswers answers)
    {
        AnsiConsole.Write(new Rule("[bold]Step 5 of 7: Output Options[/]"));
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[grey]json_stdout is always enabled (required for scheduled task integration).[/]");
        AnsiConsole.WriteLine();

        var options = AnsiConsole.Prompt(
            new MultiSelectionPrompt<string>()
                .Title("Additional outputs:")
                .NotRequired()
                .AddChoices("json_file", "prometheus"));

        answers.IncludeJsonFile = options.Contains("json_file");
        answers.IncludePrometheus = options.Contains("prometheus");

        AnsiConsole.WriteLine();
    }

    private static void RunStep6_Thresholds(WizardAnswers answers)
    {
        AnsiConsole.Write(new Rule("[bold]Step 6 of 7: Alert Thresholds[/]"));
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[grey]Configure alert thresholds (press Enter to accept defaults).[/]");
        AnsiConsole.WriteLine();

        answers.FreeSpaceWarningPct = AnsiConsole.Ask("Free space warning threshold (%):", 15);
        answers.FreeSpaceCriticalPct = AnsiConsole.Ask("Free space critical threshold (%):", 5);
        answers.OverageMultiplier = AnsiConsole.Ask("Retention overage multiplier:", 1.5);
        answers.OrphanDetection = AnsiConsole.Confirm("Enable orphan backup detection?", true);
        answers.SessionLookbackHours = AnsiConsole.Ask("Session lookback (hours):", 24);

        AnsiConsole.WriteLine();
    }

    private static void RunStep7_Review(WizardAnswers answers, string outputPath)
    {
        AnsiConsole.Write(new Rule("[bold]Step 7 of 7: Review and Save[/]"));
        AnsiConsole.WriteLine();

        // Summary table
        var table = new Table();
        table.Title = new TableTitle("Configuration Summary");
        table.AddColumn("[bold]Setting[/]");
        table.AddColumn("[bold]Value[/]");
        table.Border(TableBorder.Rounded);

        table.AddRow("Servers", answers.Servers.Count.ToString());
        foreach (var s in answers.Servers)
            table.AddRow($"  {s.Name}", $"{s.Type} — {s.Url}");

        table.AddRow("Free space warning", $"{answers.FreeSpaceWarningPct}%");
        table.AddRow("Free space critical", $"{answers.FreeSpaceCriticalPct}%");
        table.AddRow("Overage multiplier", answers.OverageMultiplier.ToString("F1"));
        table.AddRow("Orphan detection", answers.OrphanDetection ? "Enabled" : "Disabled");
        table.AddRow("Session lookback", $"{answers.SessionLookbackHours}h");
        table.AddRow("Output path", outputPath);

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();

        if (!AnsiConsole.Confirm("Save this configuration?", true))
        {
            AnsiConsole.MarkupLine("[yellow]Setup cancelled. No file was written.[/]");
            return;
        }

        // Build config and write
        var config = ConfigBuilder.Build(answers);
        var yaml = ConfigBuilder.ToYaml(config);

        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        File.WriteAllText(outputPath, yaml);

        var fullPath = Path.GetFullPath(outputPath);
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[green]Configuration saved to:[/] {fullPath.EscapeMarkup()}");
        AnsiConsole.WriteLine();

        // Next steps
        AnsiConsole.Write(new Rule("[bold]Next Steps[/]"));
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("  [bold]1.[/] Test connectivity:");
        AnsiConsole.MarkupLine($"       [cyan]veeam-vhc-aws test-connection -c {outputPath.EscapeMarkup()}[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("  [bold]2.[/] Run all monitors:");
        AnsiConsole.MarkupLine($"       [cyan]veeam-vhc-aws all -c {outputPath.EscapeMarkup()}[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("  [bold]3.[/] Schedule automated monitoring:");
        AnsiConsole.MarkupLine($"       [cyan]veeam-vhc-aws setup --help[/] for scheduling instructions");
        AnsiConsole.WriteLine();
    }
}
