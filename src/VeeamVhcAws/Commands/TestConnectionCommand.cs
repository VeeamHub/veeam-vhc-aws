using System.CommandLine;
using System.Text.Json;
using MailKit.Net.Smtp;
using Spectre.Console;
using VeeamVhcAws.Core.Config;
using VeeamVhcAws.Core.Logging;
using VeeamVhcAws.Infrastructure;

namespace VeeamVhcAws.Commands;

public static class TestConnectionCommand
{
    public static Command Create()
    {
        var command = new Command("test-connection", "Test connectivity to all configured servers");
        var configOption = CommandHelpers.ConfigOption();
        command.AddOption(configOption);

        command.SetHandler((string? config) =>
        {
            var configPath = ConfigLoader.GetConfigPath(config);
            var cfg = ConfigLoader.LoadConfig(configPath);
            LoggingSetup.Setup(cfg);
            var servers = ServerContextBuilder.BuildAllServers(cfg);

            var table = new Table();
            table.Title = new TableTitle("Connection Test Results");
            table.AddColumn("Server");
            table.AddColumn("Type");
            table.AddColumn("Status");
            table.AddColumn("Details");

            foreach (var ctx in servers)
            {
                if (ctx.VbrClient != null)
                {
                    try
                    {
                        var info = ctx.VbrClient.GetServerInfo();
                        var details = JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true });
                        if (details.Length > 200) details = details[..200];
                        table.AddRow(ctx.Name, "VBR", "[green]Connected[/]", details.EscapeMarkup());
                    }
                    catch (Exception e)
                    {
                        var msg = e.Message.Length > 200 ? e.Message[..200] : e.Message;
                        table.AddRow(ctx.Name, "VBR", "[red]Failed[/]", msg);
                    }
                }
                else if (ctx.VbawsClient != null)
                {
                    try
                    {
                        var sessions = ctx.VbawsClient.GetHealthCheckSessions();
                        table.AddRow(ctx.Name, "VBAWS", "[green]Connected[/]",
                            $"Retrieved {sessions.Count} health check sessions");
                    }
                    catch (Exception e)
                    {
                        var msg = e.Message.Length > 200 ? e.Message[..200] : e.Message;
                        table.AddRow(ctx.Name, "VBAWS", "[red]Failed[/]", msg);
                    }
                }
            }

            // Test SMTP outputs
            var outputConfigs = cfg.GetListOfSections("output");
            foreach (var output in outputConfigs)
            {
                if (output.Get("type", "") != "email") continue;

                var host = output.Get("smtp_host", "");
                var port = output.Get("smtp_port", 587);
                var username = output.Get("smtp_username", "");
                var password = PasswordObfuscator.Deobfuscate(output.Get("smtp_password", ""));
                var useTls = output.Get("use_tls", true);
                var label = $"{host}:{port}";

                try
                {
                    using var client = new SmtpClient();
                    client.Connect(host, port, useTls
                        ? MailKit.Security.SecureSocketOptions.StartTls
                        : MailKit.Security.SecureSocketOptions.None);
                    if (!string.IsNullOrEmpty(username))
                        client.Authenticate(username, password);
                    client.Disconnect(true);
                    table.AddRow(label, "SMTP", "[green]Connected[/]",
                        !string.IsNullOrEmpty(username) ? "Auth OK" : "No auth configured");
                }
                catch (Exception e)
                {
                    var msg = e.Message.Length > 200 ? e.Message[..200] : e.Message;
                    table.AddRow(label, "SMTP", "[red]Failed[/]", msg);
                }
            }

            if (servers.Count == 0 && outputConfigs.All(o => o.Get("type", "") != "email"))
                AnsiConsole.MarkupLine("[yellow]No servers configured.[/]");
            else
                AnsiConsole.Write(table);
        }, configOption);

        return command;
    }
}
