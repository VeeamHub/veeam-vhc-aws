using System.CommandLine;
using System.Reflection;
using Spectre.Console;

namespace VeeamVhcAws.Commands;

public static class SetupCommand
{
    public static Command Create()
    {
        var command = new Command("setup", "Interactive first-time setup — creates a config file");
        var outputOption = new Option<string?>(new[] { "--output", "-o" }, "Config file output path");
        command.AddOption(outputOption);

        command.SetHandler((string? configPath) =>
        {
            configPath ??= "./veeam-vhc-aws.yaml";

            if (File.Exists(configPath))
            {
                if (!AnsiConsole.Confirm($"Config file {configPath} already exists. Overwrite?", false))
                {
                    AnsiConsole.MarkupLine("[yellow]Setup cancelled.[/]");
                    return;
                }
            }

            // Try embedded resource first
            var assembly = Assembly.GetExecutingAssembly();
            var resourceStream = assembly.GetManifestResourceStream("VeeamVhcAws.config.example.yaml");

            if (resourceStream != null)
            {
                using var reader = new StreamReader(resourceStream);
                var content = reader.ReadToEnd();
                File.WriteAllText(configPath, content);
            }
            else
            {
                // Try filesystem locations
                var baseDirs = new List<string>
                {
                    Path.GetDirectoryName(assembly.Location) ?? "",
                    AppDomain.CurrentDomain.BaseDirectory,
                };

                string? examplePath = null;
                foreach (var baseDir in baseDirs)
                {
                    var candidates = new[]
                    {
                        Path.Combine(baseDir, "config", "example.yaml"),
                        Path.Combine(baseDir, "example.yaml"),
                    };
                    foreach (var candidate in candidates)
                    {
                        if (File.Exists(candidate))
                        {
                            examplePath = candidate;
                            break;
                        }
                    }
                    if (examplePath != null) break;
                }

                if (examplePath != null)
                {
                    File.Copy(examplePath, configPath, overwrite: true);
                }
                else
                {
                    // Generate minimal config inline
                    File.WriteAllText(configPath, MinimalConfig);
                }
            }

            var fullPath = Path.GetFullPath(configPath);
            AnsiConsole.MarkupLine($"\n[green]Config file created:[/] {fullPath}");
            AnsiConsole.MarkupLine("\n[bold]Next steps:[/]");
            AnsiConsole.MarkupLine($"  1. Edit [cyan]{configPath}[/] with your server details");
            AnsiConsole.MarkupLine($"  2. Test connectivity:  [cyan]veeam-vhc-aws test-connection -c {configPath}[/]");
            AnsiConsole.MarkupLine($"  3. Run all monitors:   [cyan]veeam-vhc-aws all -c {configPath}[/]");
            AnsiConsole.WriteLine();
        }, outputOption);

        return command;
    }

    private const string MinimalConfig = @"# veeam-vhc-aws.yaml — Edit this file with your server details
global:
  timeout_seconds: 30
  retry_count: 2
  logging:
    level: INFO
    file: ./veeam-vhc-aws.log

servers:
  - name: my-vbr
    type: vbr
    url: https://vbr-server:9419
    username: DOMAIN\\backupadmin
    password: """"
    api_version: ""1.3-rev1""
    verify_ssl: false

  # Uncomment for VBAWS:
  # - name: my-vbaws
  #   type: vbaws
  #   url: https://vbaws-appliance
  #   username: admin
  #   password: """"
  #   verify_ssl: false

output:
  - type: json_stdout

repo_health:
  enabled: true
  thresholds:
    free_space_warning_pct: 15
    free_space_critical_pct: 5

retention:
  enabled: true
  thresholds:
    overage_multiplier: 1.5
    orphan_detection: true
  session_lookback_hours: 48
  # exclude_jobs:
  #   - ""job name substring to mute""
  # exclude_session_errors:
  #   - ""error message regex to mute""

worker_health:
  enabled: true
  lookback_hours: 24
";
}
