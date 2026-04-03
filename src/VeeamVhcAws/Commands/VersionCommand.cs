using System.CommandLine;
using System.Reflection;

namespace VeeamVhcAws.Commands;

public static class VersionCommand
{
    public static Command Create()
    {
        var command = new Command("version", "Print the veeam-vhc-aws version");
        command.SetHandler(() =>
        {
            var version = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion ?? "0.0.0";
            Console.WriteLine($"veeam-vhc-aws v{version}");
        });
        return command;
    }
}
