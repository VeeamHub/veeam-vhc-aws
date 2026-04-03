using System.CommandLine;
using System.Reflection;
using Serilog;
using VeeamVhcAws.Commands;

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
            VersionCommand.Create(),
            SetupCommand.Create(),
        };

        // Welcome screen when no subcommand provided
        rootCommand.SetHandler(() =>
        {
            var version = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion ?? "0.0.0";

            Console.WriteLine($"veeam-vhc-aws v{version}");
            Console.WriteLine();
            Console.WriteLine("Veeam VHC AWS — CLI toolkit for monitoring Veeam infrastructure");
            Console.WriteLine();
            Console.WriteLine("Commands:");
            Console.WriteLine("  all              Run all enabled monitors on all servers");
            Console.WriteLine("  repo-health      Run the repository health monitor");
            Console.WriteLine("  retention        Run the retention monitor");
            Console.WriteLine("  worker-health    Run the worker health monitor");
            Console.WriteLine("  summary          Run all monitors and emit a daily summary");
            Console.WriteLine("  serve            Start Prometheus HTTP server with periodic monitoring");
            Console.WriteLine("  test-connection  Test connectivity to all configured servers");
            Console.WriteLine("  diagnose         Show known error patterns or match against them");
            Console.WriteLine("  version          Print the version");
            Console.WriteLine("  setup            Create a config file from the bundled example");
            Console.WriteLine();
            Console.WriteLine("Use --help for more information about a command.");
        });

        return rootCommand.InvokeAsync(args).GetAwaiter().GetResult();
    }
}
