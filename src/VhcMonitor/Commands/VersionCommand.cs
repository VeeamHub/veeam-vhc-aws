using System.CommandLine;
using System.Reflection;

namespace VhcMonitor.Commands;

public static class VersionCommand
{
    public static Command Create()
    {
        var command = new Command("version", "Print the vhc-monitor version");
        command.SetHandler(() =>
        {
            var version = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion ?? "0.0.0";
            Console.WriteLine($"vhc-monitor v{version}");
        });
        return command;
    }
}
