using System.CommandLine;
using System.CommandLine.Invocation;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
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
        var requireTokenOption = new Option<bool>(
            "--require-token",
            "Require the auth token even when the bind address is loopback");

        command.AddOption(configOption);
        command.AddOption(portOption);
        command.AddOption(bindOption);
        command.AddOption(noBrowserOption);
        command.AddOption(regenTokenOption);
        command.AddOption(requireTokenOption);

        command.SetHandler(async (InvocationContext context) =>
        {
            var parsed = context.ParseResult;
            var config = parsed.GetValueForOption(configOption);
            var port = parsed.GetValueForOption(portOption);
            var bind = parsed.GetValueForOption(bindOption)!;
            var noBrowser = parsed.GetValueForOption(noBrowserOption);
            var regenToken = parsed.GetValueForOption(regenTokenOption);
            var requireToken = parsed.GetValueForOption(requireTokenOption);

            var configPath = ConfigLoader.GetConfigPath(config);
            SecretKeyStore.BindProcessDirectory(configPath);
            if (File.Exists(configPath))
            {
                var cfg = ConfigLoader.LoadConfig(configPath);
                LoggingSetup.Setup(cfg, configPath);
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
            var auth = BuildAuthOptions(isLoopback, requireToken, tokenPath);

            var existing = await DetectPortAsync(port, PortProbeTimeout);
            if (existing == PortState.Foreign)
            {
                Console.Error.WriteLine(
                    $"Port {port} is already in use by another program. Choose a different port with --port <number>.");
                context.ExitCode = 1;
                return;
            }
            if (existing is PortState.OursUi or PortState.OursUiTokenRequired)
            {
                var runningUrl = $"http://127.0.0.1:{port}";
                Console.WriteLine($"The admin UI is already running at {runningUrl}");
                // The running instance may require the token even when this launch would not
                // (it was started with --bind or --require-token). Reuse the token it reads.
                var token = auth.RequireToken ? auth.Token
                    : existing == PortState.OursUiTokenRequired ? ReadExistingToken(tokenPath) : null;
                if (!noBrowser && TerminalCapabilities.IsInteractive)
                    OpenBrowser(string.IsNullOrEmpty(token) ? runningUrl : $"{runningUrl}/?t={token}");
                return;
            }

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
        });

        return command;
    }

    internal enum PortState { Free, OursUi, OursUiTokenRequired, Foreign }

    private static readonly TimeSpan PortProbeTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Decides whether the target port is free, held by a running VHC admin UI, or held by something else.
    /// Always probes 127.0.0.1 (even when the UI binds 0.0.0.0) and never sends credentials.
    /// </summary>
    internal static async Task<PortState> DetectPortAsync(int port, TimeSpan timeout)
    {
        // Ask the OS before touching the network. Windows refuses a connect to a closed loopback
        // port only after ~2 s of SYN retries, which outran the probe timeout and made every free
        // port look occupied, so `ui` exited with "Port ... is already in use" on every launch.
        if (!HasLoopbackReachableListener(port))
            return PortState.Free;

        using (var tcp = new TcpClient())
        {
            using var connectCts = new CancellationTokenSource(timeout);
            try
            {
                await tcp.ConnectAsync(System.Net.IPAddress.Loopback, port, connectCts.Token);
            }
            catch (SocketException)
            {
                return PortState.Free;
            }
            catch (OperationCanceledException)
            {
                // Something accepted the SYN but never completed: the port is occupied.
                return PortState.Foreign;
            }
        }

        try
        {
            using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false })
            {
                Timeout = timeout,
            };
            using var response = await http.GetAsync($"http://127.0.0.1:{port}/", HttpCompletionOption.ResponseHeadersRead);
            if (!response.Headers.TryGetValues(WebHost.IdentityHeader, out var values)
                || !values.Contains(WebHost.IdentityHeaderValue))
                return PortState.Foreign;
            return response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                ? PortState.OursUiTokenRequired
                : PortState.OursUi;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            return PortState.Foreign;
        }
    }

    private static bool HasLoopbackReachableListener(int port)
    {
        try
        {
            return IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(e =>
                e.Port == port
                && (IPAddress.IsLoopback(e.Address)
                    || e.Address.Equals(IPAddress.Any)
                    || e.Address.Equals(IPAddress.IPv6Any)));
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException)
        {
            return true; // The OS can't say; let the network probe decide.
        }
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

    internal static AuthOptions BuildAuthOptions(bool isLoopback, bool requireToken, string tokenPath)
    {
        if (isLoopback && !requireToken)
            return new AuthOptions { RequireToken = false, Token = "" };

        var token = ReadOrCreateToken(tokenPath);
        return new AuthOptions { RequireToken = true, Token = token };
    }

    private static string? ReadExistingToken(string path)
    {
        try
        {
            var existing = File.Exists(path) ? File.ReadAllText(path).Trim() : "";
            return existing.Length >= 32 ? existing : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
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

        var ownerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        var options = new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = ownerOnly;

        using (var stream = new FileStream(path, options))
        using (var writer = new StreamWriter(stream))
        {
            writer.Write(token);
        }

        if (OperatingSystem.IsWindows())
            RestrictWindowsTokenAcl(path);
        else
            File.SetUnixFileMode(path, ownerOnly);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]

    private static void RestrictWindowsTokenAcl(string path)
    {
        var user = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
        var args = string.Join(' ',
            QuoteIcacls(path),
            "/inheritance:r",
            "/grant:r",
            QuoteIcacls($"{user}:(R,W)"),
            QuoteIcacls(@"NT AUTHORITY\SYSTEM:(R,W)"),
            QuoteIcacls(@"BUILTIN\Administrators:(R,W)"));
        using var proc = Process.Start(new ProcessStartInfo
        {
            FileName = "icacls",
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        proc?.WaitForExit(5000);
    }

    private static string QuoteIcacls(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";

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
