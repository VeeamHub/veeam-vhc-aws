using System.CommandLine;
using System.Text.RegularExpressions;
using VeeamVhcAws.Core.Config;

namespace VeeamVhcAws.Commands;

public static class EncryptConfigCommand
{
    internal static readonly Regex PasswordLineRegex = new(
        @"^(\s*(?:smtp_)?password:\s*)(?![ \t]*""?ENC:)""?([^""#\n]+?)""?\s*$",
        RegexOptions.Compiled);

    public static Command Create()
    {
        var command = new Command("encrypt-config", "Obfuscate plaintext passwords in config file in-place");
        var configOption = new Option<string?>("--config", "Path to config file (default: same as other commands)");
        command.AddOption(configOption);

        command.SetHandler((string? configArg) =>
        {
            var path = ConfigLoader.GetConfigPath(configArg);

            if (!File.Exists(path))
            {
                Console.Error.WriteLine($"Config file not found: {path}");
                return;
            }

            var lines = File.ReadAllLines(path);
            var count = 0;

            for (var i = 0; i < lines.Length; i++)
            {
                var match = PasswordLineRegex.Match(lines[i]);
                if (!match.Success)
                    continue;

                var prefix = match.Groups[1].Value;
                var plaintext = match.Groups[2].Value.Trim();

                if (string.IsNullOrEmpty(plaintext))
                    continue;

                var obfuscated = PasswordObfuscator.Obfuscate(plaintext);
                lines[i] = $"{prefix}\"{obfuscated}\"";
                count++;
            }

            if (count == 0)
            {
                Console.WriteLine("No plaintext passwords found.");
                return;
            }

            File.WriteAllLines(path, lines);
            Console.WriteLine($"Obfuscated {count} password(s) in {path}");
        }, configOption);

        return command;
    }
}
