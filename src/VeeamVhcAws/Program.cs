using System.CommandLine;
using System.Reflection;
using Serilog;
using VeeamVhcAws.Commands;
using VeeamVhcAws.Ui;
using Spectre.Console;

namespace VeeamVhcAws;

public class Program
{
    public static int Main(string[] args)
    {
        // No-console guard (SYSTEM account, Windows services)
        if (!Environment.UserInteractive)
        {
            Console.SetOut(TextWriter.Null);
            Console.SetError(TextWriter.Null);
        }

        // Crash log handler
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
        {
            var crashPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "VHC", "veeam-vhc-aws-crash.log");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(crashPath)!);
                File.AppendAllText(crashPath, $"[{DateTime.UtcNow:O}] {eventArgs.ExceptionObject}\n\n");
            }
            catch { /* best effort */ }
        };

        // Apply --no-interactive early if present in args (before command parsing)
        if (args.Contains("--no-interactive"))
            TerminalCapabilities.SetNoInteractive();

        var noInteractiveOption = new Option<bool>(
            "--no-interactive",
            "Suppress all interactive terminal output (auto-detected for non-TTY environments)");

        var rootCommand = new RootCommand("VHC monitoring toolkit")
        {
            AllCommand.Create(),
            RepoHealthCommand.Create(),
            RetentionCommand.Create(),
            WorkerHealthCommand.Create(),
            SummaryCommand.Create(),
            ServeCommand.Create(),
            TestConnectionCommand.Create(),
            DiagnoseCommand.Create(),
            CapturesCommand.Create(),
            VersionCommand.Create(),
            SetupCommand.Create(),
            EncryptConfigCommand.Create(),
            UiCommand.Create(),
        };

        rootCommand.AddGlobalOption(noInteractiveOption);

        // Welcome screen when no subcommand provided
        rootCommand.SetHandler((bool noInteractive) =>
        {
            if (noInteractive)
                TerminalCapabilities.SetNoInteractive();

            var version = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion ?? "0.0.0";

            (string Command, string Description)[] commands =
            [
                ("all",            "Run all enabled monitors on all servers"),
                ("repo-health",    "Run the repository health monitor"),
                ("retention",      "Run the retention monitor"),
                ("worker-health",  "Run the worker health monitor"),
                ("summary",        "Run all monitors and emit a daily summary"),
                ("serve",          "Start Prometheus HTTP server with periodic monitoring"),
                ("test-connection","Test connectivity to all configured servers and SMTP"),
                ("diagnose",       "Show known error patterns or match against them"),
                ("captures",       "View and manage captured errors and suppressions"),
                ("version",        "Print the version"),
                ("setup",          "Interactive wizard or --template to copy example config"),
                ("encrypt-config", "Obfuscate plaintext passwords in config file in-place"),
                ("ui",             "Launch the web admin GUI (browser-based)"),
            ];

            if (TerminalCapabilities.IsInteractive)
            {
                AnsiConsole.Write(new FigletText("VHC").Color(Color.Green));
                AnsiConsole.MarkupLine($"[bold]veeam-vhc-aws[/] [grey]v{version}[/]");
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine("[bold]Veeam VHC AWS[/] — CLI toolkit for monitoring Veeam infrastructure");
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine("[bold]Commands:[/]");
                foreach (var (cmd, desc) in commands)
                    AnsiConsole.MarkupLine($"  [cyan]{cmd,-16}[/] {desc}");
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine("[grey]Use --help for more information about a command.[/]");

                var defaultConfig = Core.Config.ConfigLoader.GetConfigPath(null);
                if (FirstRunDetector.CheckAndPrompt(defaultConfig))
                    SetupWizard.Run(defaultConfig);
            }
            else
            {
                Console.WriteLine($"veeam-vhc-aws v{version}");
                Console.WriteLine();
                Console.WriteLine("Veeam VHC AWS — CLI toolkit for monitoring Veeam infrastructure");
                Console.WriteLine();
                Console.WriteLine("Commands:");
                foreach (var (cmd, desc) in commands)
                    Console.WriteLine($"  {cmd,-16} {desc}");
                Console.WriteLine();
                Console.WriteLine("Use --help for more information about a command.");
            }
        }, noInteractiveOption);

        return rootCommand.InvokeAsync(args).GetAwaiter().GetResult();
    }
}
