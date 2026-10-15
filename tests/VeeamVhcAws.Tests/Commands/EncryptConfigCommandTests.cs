using System.CommandLine;
using Xunit;
using VeeamVhcAws.Commands;
using VeeamVhcAws.Core.Config;

namespace VeeamVhcAws.Tests.Commands;

/// <summary>
/// encrypt-config must protect every key the web UI treats as a secret, and the value it
/// encrypts must be the value the app would have read from the YAML (quotes and escapes resolved).
/// </summary>
public class EncryptConfigCommandTests
{
    [Theory]
    [InlineData("    api_token: tok-123", "tok-123")]
    [InlineData("    client_secret: \"sec-456\"", "sec-456")]
    [InlineData("    db_password: pw-789", "pw-789")]
    [InlineData("    password: 'P@ss\\word!'", "P@ss\\word!")]
    [InlineData("    password: 'it''s'", "it's")]
    [InlineData("    password: \"say \\\"hi\\\"\"", "say \"hi\"")]
    [InlineData("    smtp_password: \"hunter2\"   # run 'veeam-vhc-aws encrypt-config' to obfuscate", "hunter2")]
    [InlineData("    smtp_password: hunter2 # trailing comment", "hunter2")]
    [InlineData("  - password: first-key\n    type: email", "first-key")]
    public async Task EncryptConfig_ProtectsSecretWithTheValueTheAppReads(string line, string expected)
    {
        var entry = line.StartsWith("  - ", StringComparison.Ordinal) ? line : "  - type: email\n" + line;
        var (dir, path) = await WriteConfig("output:\n" + entry + "\n");
        try
        {
            var before = LoadSecret(path);
            Assert.Equal(expected, before);

            await Run(path);

            var updated = await File.ReadAllTextAsync(path);
            Assert.DoesNotContain(expected, updated);
            Assert.Contains("ENC:", updated);
            Assert.Equal(expected, LoadSecret(path));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task EncryptConfig_KeepsTrailingComment()
    {
        var (dir, path) = await WriteConfig(
            "output:\n  - type: email\n    smtp_password: \"hunter2\"   # run encrypt-config\n");
        try
        {
            await Run(path);

            var line = (await File.ReadAllLinesAsync(path)).Single(l => l.Contains("smtp_password"));
            Assert.StartsWith("    smtp_password: \"ENC:", line);
            Assert.EndsWith("   # run encrypt-config", line);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task EncryptConfig_LeavesCurrentEncAndNonSecretLinesUntouched()
    {
        var (dir, path) = await WriteConfig("output: []\n");
        try
        {
            SecretKeyStore.BindThreadDirectory(path);
            var current = PasswordObfuscator.Obfuscate("already");
            var yaml = string.Join('\n',
                "servers:",
                "  - name: lab",
                "    url: https://vbr:9419",
                "    username: DOMAIN\\admin",
                $"    password: \"{current}\"",
                "    verify_ssl: true",
                "output:",
                "  - type: webhook",
                "    url: https://hooks.example/T000/B000/XXXX",
                "    smtp_password: \"\"",
                "    api_token: ''",
                "");
            await File.WriteAllTextAsync(path, yaml);

            await Run(path);

            Assert.Equal(yaml, await File.ReadAllTextAsync(path));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    private static async Task<(DirectoryInfo Dir, string Path)> WriteConfig(string yaml)
    {
        var dir = Directory.CreateTempSubdirectory("vhc-encrypt-");
        var path = Path.Combine(dir.FullName, "veeam-vhc-aws.yaml");
        await File.WriteAllTextAsync(path, yaml);
        return (dir, path);
    }

    private static async Task Run(string path)
    {
        var exit = await EncryptConfigCommand.Create().InvokeAsync(new[] { "--config", path });
        Assert.Equal(0, exit);
    }

    private static string LoadSecret(string path)
    {
        SecretKeyStore.BindThreadDirectory(path);
        var entry = ConfigLoader.LoadConfig(path).GetListOfSections("output").Single();
        var (_, value) = entry.Single(kv => kv.Key != "type");
        return PasswordObfuscator.Deobfuscate(value?.ToString() ?? "");
    }
}
