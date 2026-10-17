using System.Text.RegularExpressions;
using YamlDotNet.Serialization;

namespace VeeamVhcAws.Core.Config;

/// <summary>
/// One definition of which config keys hold secrets, shared by the web UI (mask and encrypt
/// on save) and <c>encrypt-config</c> (rewrite plaintext on disk).
/// </summary>
public static class ConfigSecrets
{
    private static readonly string[] KeyFragments = ["password", "token", "secret"];

    // key: value [# comment] on one line, optionally as the first key of a list item.
    // The value is a single-quoted, double-quoted, or plain scalar. A plain scalar ends at " #".
    private static readonly Regex KeyValueLine = new(
        @"^(?<prefix>[ \t]*(?:-[ \t]+)?(?<key>[A-Za-z0-9_.-]+):[ \t]+)" +
        @"(?<value>'(?:[^'\n]|'')*'|""(?:[^""\\\n]|\\.)*""|[^\s'""#|>&*!\[\]{}](?:[^\n#]|(?<=\S)#)*?)" +
        @"(?<suffix>[ \t]+#.*|[ \t]*)$",
        RegexOptions.Compiled);

    private static readonly IDeserializer ScalarReader = new DeserializerBuilder().Build();

    public static bool IsSecretKey(string key) =>
        KeyFragments.Any(fragment => key.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Encrypts plaintext secret values and rewraps legacy ENC: values in place. Keys, indentation
    /// and trailing comments are kept. Current ENC: values and non-secret keys are not touched.
    /// Returns the number of lines rewritten.
    /// </summary>
    public static int ProtectLines(string[] lines)
    {
        var count = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            var match = KeyValueLine.Match(lines[i]);
            if (!match.Success || !IsSecretKey(match.Groups["key"].Value))
                continue;

            var value = ReadScalar(match.Groups["value"].Value);
            if (string.IsNullOrEmpty(value))
                continue;

            string protectedValue;
            if (value.StartsWith("ENC:", StringComparison.Ordinal))
            {
                if (!PasswordObfuscator.IsLegacyCiphertext(value))
                    continue;
                protectedValue = PasswordObfuscator.Obfuscate(PasswordObfuscator.Deobfuscate(value));
            }
            else
            {
                protectedValue = PasswordObfuscator.Obfuscate(value);
            }

            lines[i] = $"{match.Groups["prefix"].Value}\"{protectedValue}\"{match.Groups["suffix"].Value}";
            count++;
        }
        return count;
    }

    // Resolve quotes and escapes the same way ConfigLoader does, so the encrypted value is
    // exactly the value the app would have read.
    private static string? ReadScalar(string raw)
    {
        try
        {
            return ScalarReader.Deserialize<string?>(ConfigLoader.NormalizeBackslashes(raw));
        }
        catch (YamlDotNet.Core.YamlException)
        {
            return null;
        }
    }
}
