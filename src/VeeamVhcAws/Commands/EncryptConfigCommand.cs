using System.CommandLine;
using System.Text.RegularExpressions;
using VeeamVhcAws.Core.Config;

namespace VeeamVhcAws.Commands;

public static class EncryptConfigCommand
{
    internal static readonly Regex PasswordLineRegex = new(
        @"^(\s*(?:smtp_)?password:\s*)(?![ \t]*""?ENC:)""?([^""#\n]+?)""?\s*$",
        RegexOptions.Compiled);

    internal static readonly Regex EncryptedPasswordLineRegex = new(
        @"^(\s*(?:smtp_)?password:\s*)""?(ENC:[^""#\s]+)""?\s*$",
        RegexOptions.Compiled);

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
            var count = 0;

            for (var i = 0; i < lines.Length; i++)
            {
                var match = PasswordLineRegex.Match(lines[i]);
                if (match.Success)
                {
                    var prefix = match.Groups[1].Value;
                    var plaintext = match.Groups[2].Value.Trim();
                    if (string.IsNullOrEmpty(plaintext))
                        continue;
                    lines[i] = $"{prefix}\"{PasswordObfuscator.Obfuscate(plaintext)}\"";
                    count++;
                    continue;
                }

                var encrypted = EncryptedPasswordLineRegex.Match(lines[i]);
                if (!encrypted.Success)
                    continue;
                var stored = encrypted.Groups[2].Value;
                if (!PasswordObfuscator.IsLegacyCiphertext(stored))
                    continue;
                var plain = PasswordObfuscator.Deobfuscate(stored);
                lines[i] = $"{encrypted.Groups[1].Value}\"{PasswordObfuscator.Obfuscate(plain)}\"";
                count++;
            }

            var emailEdits = LegacyEmailKeys.MigrateLines(lines);
            if (count == 0 && emailEdits == 0)
            {
                Console.WriteLine("No plaintext or legacy passwords found.");
                return;
            }

            File.WriteAllLines(path, lines);
            if (count > 0)
                Console.WriteLine($"Protected {count} password(s) in {path}");
            if (emailEdits > 0)
                Console.WriteLine($"Migrated {emailEdits} legacy email key(s) in {path}");
        }, configOption);

        return command;
    }
}
