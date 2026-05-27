using System.CommandLine;
using System.Diagnostics;
using Spectre.Console;
using VeeamVhcAws.Core.Config;
using VeeamVhcAws.Core.Logging;
using VeeamVhcAws.Ui;
using VeeamVhcAws.Web;

namespace VeeamVhcAws.Commands;

public static class UiCommand
{
    public static Command Create()
    {
        var command = new Command("ui", "Launch the web admin GUI (browser-based)");
        var configOption = CommandHelpers.ConfigOption();
        var portOption = new Option<int>(new[] { "--port", "-p" }, () => 9101, "HTTP port to listen on");
        var bindOption = new Option<string>("--bind", () => "127.0.0.1", "Address to bind (default: 127.0.0.1 = localhost-only)");
        var noBrowserOption = new Option<bool>("--no-browser", "Do not auto-open the browser");
        var regenTokenOption = new Option<bool>("--regen-token", "Regenerate the auth token and exit");

        command.AddOption(configOption);
        command.AddOption(portOption);
        command.AddOption(bindOption);
        command.AddOption(noBrowserOption);
        command.AddOption(regenTokenOption);

        command.SetHandler(async (string? config, int port, string bind, bool noBrowser, bool regenToken) =>
        {
            var configPath = ConfigLoader.GetConfigPath(config);
            if (File.Exists(configPath))
            {
                var cfg = ConfigLoader.LoadConfig(configPath);
                LoggingSetup.Setup(cfg);
            }
            else if (TerminalCapabilities.IsInteractive)
            {
                AnsiConsole.MarkupLine($"[yellow]⚠ Config not found:[/] [grey]{configPath.EscapeMarkup()}[/]");
                AnsiConsole.MarkupLine($"[grey]  Servers page will be empty — add servers via the UI to create it.[/]");
            }

            var tokenPath = GetTokenPath();
            if (regenToken)
            {
                var newToken = AuthMiddleware.GenerateToken();
                WriteToken(tokenPath, newToken);
                Console.WriteLine($"New token written to {tokenPath}");
                return;
            }

            var isLoopback = IsLoopback(bind);
            var auth = BuildAuthOptions(isLoopback, tokenPath);

            var options = new WebHostOptions(configPath, bind, port);
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

            var baseUrl = $"http://{bind}:{port}";
            var displayUrl = auth.RequireToken ? $"{baseUrl}/?t={auth.Token}" : baseUrl;

            if (TerminalCapabilities.IsInteractive)
            {
                AnsiConsole.MarkupLine($"\n[bold green]Veeam VHC AWS Admin UI[/]");
                AnsiConsole.MarkupLine($"  URL:    [cyan]{displayUrl.EscapeMarkup()}[/]");
                AnsiConsole.MarkupLine($"  Config: [grey]{configPath.EscapeMarkup()}[/]");
                if (auth.RequireToken)
                {
                    AnsiConsole.MarkupLine($"  Auth:   [yellow]token required (bind={bind.EscapeMarkup()})[/]");
                    AnsiConsole.MarkupLine($"  Token:  [grey]{auth.Token.EscapeMarkup()}[/]");
                    AnsiConsole.MarkupLine($"  Stored: [grey]{tokenPath.EscapeMarkup()}[/]");
                }
                else
                {
                    AnsiConsole.MarkupLine($"  Auth:   [grey]none (localhost-only)[/]");
                }
                AnsiConsole.MarkupLine($"\n[grey]Press Ctrl+C to stop.[/]\n");
            }
            else
            {
                Console.WriteLine($"Veeam VHC AWS Admin UI: {displayUrl} | Config: {configPath}");
            }

            if (!noBrowser && TerminalCapabilities.IsInteractive)
                OpenBrowser(displayUrl);

            try
            {
                await WebHost.RunAsync(options, auth, cts.Token);
            }
            catch (OperationCanceledException) { /* clean shutdown */ }
        }, configOption, portOption, bindOption, noBrowserOption, regenTokenOption);

        return command;
    }

    private static bool IsLoopback(string bind) =>
        bind == "127.0.0.1" || bind == "localhost" || bind == "::1";

    private static string GetTokenPath()
    {
        var dir = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "VHC")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".vhc");
        return Path.Combine(dir, "ui-token.txt");
    }

    private static AuthOptions BuildAuthOptions(bool isLoopback, string tokenPath)
    {
        if (isLoopback)
            return new AuthOptions { RequireToken = false, Token = "" };

        var token = ReadOrCreateToken(tokenPath);
        return new AuthOptions { RequireToken = true, Token = token };
    }

    private static string ReadOrCreateToken(string path)
    {
        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path).Trim();
            if (existing.Length >= 32) return existing;
        }
        var token = AuthMiddleware.GenerateToken();
        WriteToken(path, token);
        return token;
    }

    private static void WriteToken(string path, string token)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, token);
    }

    private static void OpenBrowser(string url)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            else if (OperatingSystem.IsMacOS())
                Process.Start("open", url);
            else if (OperatingSystem.IsLinux())
                Process.Start("xdg-open", url);
        }
        catch { /* best effort */ }
    }
}
