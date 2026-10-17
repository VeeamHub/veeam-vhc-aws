using System.CommandLine;
using System.Security.Cryptography;
using System.Text;
using Xunit;
using VeeamVhcAws.Core.Config;
using VeeamVhcAws.Commands;

namespace VeeamVhcAws.Tests.Core.Config;

public class PasswordObfuscatorTests
{
    [Fact]
    public void Obfuscate_ThenDeobfuscate_ReturnsOriginal()
    {
        var original = "MyS3cur3P@ssw0rd!";
        var obfuscated = PasswordObfuscator.Obfuscate(original);
        var result = PasswordObfuscator.Deobfuscate(obfuscated);
        Assert.Equal(original, result);
    }

    [Fact]
    public void Deobfuscate_PlaintextPassthrough()
    {
        var plaintext = "plainpassword";
        var result = PasswordObfuscator.Deobfuscate(plaintext);
        Assert.Equal(plaintext, result);
    }

    [Fact]
    public void Deobfuscate_EmptyString_ReturnsEmpty()
    {
        var result = PasswordObfuscator.Deobfuscate(string.Empty);
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void Obfuscate_EmptyString_ReturnsEmpty()
    {
        var result = PasswordObfuscator.Obfuscate(string.Empty);
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void Deobfuscate_CorruptValue_ThrowsInvalidOperationException()
    {
        var corrupt = "ENC:thisisnotvalidbase64!!!";
        Assert.Throws<InvalidOperationException>(() => PasswordObfuscator.Deobfuscate(corrupt));
    }

    [Fact]
    public void Obfuscate_ProducesENCPrefix()
    {
        var obfuscated = PasswordObfuscator.Obfuscate("anypassword");
        Assert.StartsWith("ENC:", obfuscated);
    }

    [Fact]
    public void Obfuscate_TwoCallsProduceDifferentCiphertext()
    {
        var password = "samepassword";
        var first = PasswordObfuscator.Obfuscate(password);
        var second = PasswordObfuscator.Obfuscate(password);
        // Random nonces ensure ciphertext differs between calls
        Assert.NotEqual(first, second);
        // Both must still decrypt correctly
        Assert.Equal(password, PasswordObfuscator.Deobfuscate(first));
        Assert.Equal(password, PasswordObfuscator.Deobfuscate(second));
    }

    [Fact]
    public void Deobfuscate_ReadsLegacyPublishedPassphrase()
    {
        var legacy = LegacyEncrypt("verify-secret");
        Assert.Equal("verify-secret", PasswordObfuscator.Deobfuscate(legacy));
    }

    [Fact]
    public void Obfuscate_WritesAnOwnerOnlyKeyFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "vhc-key-" + Guid.NewGuid().ToString("n"));
        try
        {
            string stored;
            using (SecretKeyStore.UseKeyFile(path))
            {
                stored = PasswordObfuscator.Obfuscate("verify-secret");
                Assert.Equal("verify-secret", PasswordObfuscator.Deobfuscate(stored));
            }

            var header = File.ReadAllBytes(path);
            Assert.Equal(new byte[] { (byte)'V', (byte)'H', (byte)'C', (byte)'K' }, header[..4]);
            if (OperatingSystem.IsWindows())
            {
                Assert.Equal((byte)2, header[4]);
                return;
            }

            Assert.Equal((byte)1, header[4]);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);
            using (SecretKeyStore.UseKeyFile(path))
                Assert.Throws<InvalidOperationException>(() => PasswordObfuscator.Obfuscate("again"));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Obfuscate_DoesNotUseThePublishedPassphrase()
    {
        var stored = PasswordObfuscator.Obfuscate("verify-secret");
        var raw = Convert.FromBase64String(stored["ENC:".Length..]);
        Assert.ThrowsAny<CryptographicException>(() => LegacyDecrypt(raw));
    }

    [Fact]
    public async Task EncryptConfig_RewrapsLegacyEncValues()
    {
        var dir = Directory.CreateTempSubdirectory("vhc-enc-");
        var path = Path.Combine(dir.FullName, "veeam-vhc-aws.yaml");
        var legacy = LegacyEncrypt("verify-secret");
        var line = $"    password: \"{legacy}\"";
        await File.WriteAllTextAsync(path, "servers:\n  - name: lab\n" + line + "\n");
        try
        {
            var command = EncryptConfigCommand.Create();
            var exit = await command.InvokeAsync(new[] { "--config", path });
            Assert.Equal(0, exit);
            var updated = await File.ReadAllTextAsync(path);
            Assert.DoesNotContain(legacy, updated);
            var stored = updated.Split('"').First(part => part.StartsWith("ENC:", StringComparison.Ordinal));
            SecretKeyStore.BindThreadDirectory(path);
            Assert.Equal("verify-secret", PasswordObfuscator.Deobfuscate(stored));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    private static string LegacyEncrypt(string plaintext)
    {
        var raw = LegacySeal(plaintext);
        return "ENC:" + Convert.ToBase64String(raw);
    }

    private static string LegacyDecrypt(byte[] combined)
    {
        const int nonceSize = 12;
        const int tagSize = 16;
        var key = Rfc2898DeriveBytes.Pbkdf2(
            "veeam-vhc-aws-config-obfuscation-v1",
            Encoding.UTF8.GetBytes("vhc-aws-salt-2024"),
            100_000,
            HashAlgorithmName.SHA256,
            32);
        var nonce = combined[..nonceSize];
        var tag = combined[^tagSize..];
        var ciphertext = combined[nonceSize..^tagSize];
        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(key, tagSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return Encoding.UTF8.GetString(plaintext);
    }

    private static byte[] LegacySeal(string plaintext)
    {
        const int nonceSize = 12;
        const int tagSize = 16;
        var key = Rfc2898DeriveBytes.Pbkdf2(
            "veeam-vhc-aws-config-obfuscation-v1",
            Encoding.UTF8.GetBytes("vhc-aws-salt-2024"),
            100_000,
            HashAlgorithmName.SHA256,
            32);
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var nonce = new byte[nonceSize];
        RandomNumberGenerator.Fill(nonce);
        var ciphertext = new byte[plaintextBytes.Length];
        var tag = new byte[tagSize];
        using var aes = new AesGcm(key, tagSize);
        aes.Encrypt(nonce, plaintextBytes, ciphertext, tag);
        var combined = new byte[nonceSize + ciphertext.Length + tagSize];
        Buffer.BlockCopy(nonce, 0, combined, 0, nonceSize);
        Buffer.BlockCopy(ciphertext, 0, combined, nonceSize, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, combined, nonceSize + ciphertext.Length, tagSize);
        return combined;
    }
}

public class ConfigSecretsTests
{
    [Theory]
    [InlineData("password")]
    [InlineData("smtp_password")]
    [InlineData("some_password_field")]
    [InlineData("api_token")]
    [InlineData("client_secret")]
    [InlineData("Password")]
    public void IsSecretKey_MatchesTheKeysTheUiEncrypts(string key) =>
        Assert.True(ConfigSecrets.IsSecretKey(key));

    [Theory]
    [InlineData("username")]
    [InlineData("smtp_username")]
    [InlineData("url")]
    [InlineData("smtp_host")]
    [InlineData("verify_ssl")]
    public void IsSecretKey_IgnoresOrdinaryKeys(string key) =>
        Assert.False(ConfigSecrets.IsSecretKey(key));

    [Theory]
    [InlineData("  password: mysecret")]
    [InlineData("  password: \"mysecret\"")]
    [InlineData("password: mysecret")]
    [InlineData("  smtp_password: mysecret")]
    [InlineData("  - password: mysecret")]
    public void ProtectLines_EncryptsPlaintextSecret(string line)
    {
        var lines = new[] { line };
        Assert.Equal(1, ConfigSecrets.ProtectLines(lines));
        var stored = lines[0].Split('"')[1];
        Assert.StartsWith("ENC:", stored);
        Assert.Equal("mysecret", PasswordObfuscator.Deobfuscate(stored));
    }

    [Theory]
    [InlineData("  username: someone")]
    [InlineData("  url: https://hooks.example/abc")]
    [InlineData("  password: \"\"")]
    [InlineData("  password:")]
    [InlineData("  password: |")]
    [InlineData("  # password: commented-out")]
    public void ProtectLines_LeavesLineAlone(string line)
    {
        var lines = new[] { line };
        Assert.Equal(0, ConfigSecrets.ProtectLines(lines));
        Assert.Equal(line, lines[0]);
    }

    [Fact]
    public void ProtectLines_SkipsCurrentEncValues()
    {
        var current = PasswordObfuscator.Obfuscate("x");
        var lines = new[] { $"  password: \"{current}\"", $"  api_token: {current}" };
        var before = lines.ToArray();
        Assert.Equal(0, ConfigSecrets.ProtectLines(lines));
        Assert.Equal(before, lines);
    }
}
