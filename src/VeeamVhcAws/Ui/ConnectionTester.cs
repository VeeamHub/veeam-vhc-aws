using MailKit.Net.Smtp;
using Spectre.Console;
using VeeamVhcAws.Core.Config;
using VeeamVhcAws.Infrastructure;

namespace VeeamVhcAws.Ui;

/// <summary>
/// Tests connectivity to all servers and SMTP outputs.
/// Uses AnsiConsole.Progress() in interactive mode, plain text in non-interactive.
/// Returns structured results that TestConnectionCommand can render.
/// </summary>
public static class ConnectionTester
{
    /// <summary>
    /// Run connectivity tests against all servers and email outputs.
    /// Shows live progress spinners in interactive mode.
    /// </summary>
    public static List<ConnectionTestResult> RunAll(
        List<ServerContext> servers,
        List<Dictionary<string, object>> outputConfigs)
    {
        var results = new List<ConnectionTestResult>();

        if (!TerminalCapabilities.IsInteractive)
        {
            // Non-interactive: run sequentially and print to stdout
            foreach (var ctx in servers)
                results.AddRange(TestServer(ctx));

            foreach (var output in outputConfigs)
            {
                if (output.Get("type", "") == "email")
                    results.Add(TestSmtp(output));
            }

            return results;
        }

        // Interactive: show progress spinners
        AnsiConsole.Progress()
            .AutoRefresh(true)
            .AutoClear(false)
            .HideCompleted(false)
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new SpinnerColumn())
            .Start(ctx =>
            {
                var tasks = new Dictionary<string, ProgressTask>();

                foreach (var server in servers)
                    tasks[server.Name] = ctx.AddTask($"Testing {server.Name}...");

                foreach (var output in outputConfigs)
                {
                    if (output.Get("type", "") != "email") continue;
                    var host = output.Get("smtp_host", "");
                    var port = output.Get("smtp_port", 587);
                    var label = $"{host}:{port}";
                    tasks[label] = ctx.AddTask($"Testing SMTP {label}...");
                }

                foreach (var server in servers)
                {
                    var serverResults = TestServer(server);
                    results.AddRange(serverResults);
                    tasks[server.Name].Increment(100);
                }

                foreach (var output in outputConfigs)
                {
                    if (output.Get("type", "") != "email") continue;
                    var host = output.Get("smtp_host", "");
                    var port = output.Get("smtp_port", 587);
                    var label = $"{host}:{port}";
                    var smtpResult = TestSmtp(output);
                    results.Add(smtpResult);
                    tasks[label].Increment(100);
                }
            });

        return results;
    }

    /// <summary>
    /// Render connection test results as a Spectre table or ASCII table.
    /// </summary>
    public static void RenderResults(List<ConnectionTestResult> results)
    {
        if (results.Count == 0)
        {
            if (TerminalCapabilities.IsInteractive)
                AnsiConsole.MarkupLine("[yellow]No servers configured.[/]");
            else
                Console.WriteLine("[WARN]  No servers configured.");
            return;
        }

        if (!TerminalCapabilities.IsInteractive)
        {
            RenderAscii(results);
            return;
        }

        var table = new Table();
        table.Title = new TableTitle("Connection Test Results");
        table.AddColumn("[bold]Server[/]");
        table.AddColumn("[bold]Type[/]");
        table.AddColumn("[bold]Status[/]");
        table.AddColumn("[bold]Details[/]");
        table.Border(TableBorder.Rounded);

        foreach (var r in results)
        {
            var status = r.Success ? "[green]Connected[/]" : "[red]Failed[/]";
            var details = r.Success
                ? ""
                : $"{r.ErrorMessage.EscapeMarkup()}{(string.IsNullOrEmpty(r.ErrorHint) ? "" : $"\n[yellow]{r.ErrorHint.EscapeMarkup()}[/]")}";

            table.AddRow(r.Target.EscapeMarkup(), r.Type.EscapeMarkup(), status, details);
        }

        AnsiConsole.WriteLine();
        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    private static List<ConnectionTestResult> TestServer(ServerContext ctx)
    {
        var results = new List<ConnectionTestResult>();

        if (ctx.VbrClient != null)
        {
            try
            {
                var info = ctx.VbrClient.GetServerInfo();
                results.Add(new ConnectionTestResult { Target = ctx.Name, Type = "VBR", Success = true });
            }
            catch (Exception e)
            {
                results.Add(new ConnectionTestResult
                {
                    Target = ctx.Name,
                    Type = "VBR",
                    Success = false,
                    ErrorMessage = Truncate(e.Message, 200),
                    ErrorHint = BuildHint(e.Message),
                });
            }
        }
        else if (ctx.VbawsClient != null)
        {
            try
            {
                var sessions = ctx.VbawsClient.GetHealthCheckSessions();
                results.Add(new ConnectionTestResult
                {
                    Target = ctx.Name,
                    Type = "VBAWS",
                    Success = true,
                    ErrorMessage = $"Retrieved {sessions.Count} health check sessions",
                });
            }
            catch (Exception e)
            {
                results.Add(new ConnectionTestResult
                {
                    Target = ctx.Name,
                    Type = "VBAWS",
                    Success = false,
                    ErrorMessage = Truncate(e.Message, 200),
                    ErrorHint = BuildHint(e.Message),
                });
            }
        }

        return results;
    }

    private static ConnectionTestResult TestSmtp(Dictionary<string, object> output)
    {
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

            return new ConnectionTestResult
            {
                Target = label,
                Type = "SMTP",
                Success = true,
                ErrorMessage = !string.IsNullOrEmpty(username) ? "Auth OK" : "No auth configured",
            };
        }
        catch (Exception e)
        {
            var msg = Truncate(e.Message, 200);
            return new ConnectionTestResult
            {
                Target = label,
                Type = "SMTP",
                Success = false,
                ErrorMessage = msg,
                ErrorHint = BuildHint(e.Message),
            };
        }
    }

    /// <summary>
    /// Build a user-friendly error hint based on common error message patterns.
    /// </summary>
    internal static string BuildHint(string errorMessage)
    {
        var msg = errorMessage.ToLowerInvariant();

        if (msg.Contains("401") || msg.Contains("unauthorized"))
            return "-- check username/password";

        if (msg.Contains("certificate") || msg.Contains("ssl") || msg.Contains("tls"))
            return "-- set verify_ssl: false or trust the cert";

        if (msg.Contains("refused") || msg.Contains("timed out") || msg.Contains("timeout"))
            return "-- check URL and firewall rules";

        if (msg.Contains("no such host") || msg.Contains("name or service not known") || msg.Contains("could not resolve"))
            return "-- check DNS or use IP address";

        return "";
    }

    private static string Truncate(string s, int max) => s.Length > max ? s[..max] : s;

    private static void RenderAscii(List<ConnectionTestResult> results)
    {
        Console.WriteLine();
        Console.WriteLine($"{"Target",-35} {"Type",-8} {"Status",-12} Details");
        Console.WriteLine(new string('-', 90));

        foreach (var r in results)
        {
            var status = r.Success ? "[OK]" : "[FAIL]";
            var details = r.Success
                ? (string.IsNullOrEmpty(r.ErrorMessage) ? "" : r.ErrorMessage)
                : $"{r.ErrorMessage}{(string.IsNullOrEmpty(r.ErrorHint) ? "" : " " + r.ErrorHint)}";
            var target = r.Target.Length > 35 ? r.Target[..32] + "..." : r.Target.PadRight(35);
            Console.WriteLine($"{target} {r.Type,-8} {status,-12} {details}");
        }

        Console.WriteLine();
    }
}
