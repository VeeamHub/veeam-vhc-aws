using System.CommandLine;
using VeeamVhcAws.Core.Config;

namespace VeeamVhcAws.Commands;

public static class EncryptConfigCommand
{
    public static Command Create()
    {
        var command = new Command("encrypt-config", "Protect plaintext and legacy ENC passwords in a config file");
        var configOption = new Option<string?>("--config", "Path to config file (default: same as other commands)");
        command.AddOption(configOption);

        command.SetHandler((string? configArg) =>
        {
            var path = ConfigLoader.GetConfigPath(configArg);
            SecretKeyStore.BindThreadDirectory(path);

            if (!File.Exists(path))
            {
                Console.Error.WriteLine($"Config file not found: {path}");
                return;
            }

            var lines = File.ReadAllLines(path);
            var count = ConfigSecrets.ProtectLines(lines);
            var emailEdits = LegacyEmailKeys.MigrateLines(lines);
            if (count == 0 && emailEdits == 0)
            {
                Console.WriteLine("No plaintext or legacy secrets found.");
                return;
            }

            File.WriteAllLines(path, lines);
            if (count > 0)
                Console.WriteLine($"Protected {count} secret(s) in {path}");
            if (emailEdits > 0)
                Console.WriteLine($"Migrated {emailEdits} legacy email key(s) in {path}");
        }, configOption);

        return command;
    }
}
