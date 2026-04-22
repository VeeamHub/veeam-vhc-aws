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
}

public class EncryptConfigCommandRegexTests
{
    [Theory]
    [InlineData("  password: mysecret")]
    [InlineData("  password: \"mysecret\"")]
    [InlineData("password: mysecret")]
    public void Regex_MatchesPasswordLine(string line)
    {
        var match = EncryptConfigCommand.PasswordLineRegex.Match(line);
        Assert.True(match.Success, $"Expected match for: {line}");
        Assert.Equal("mysecret", match.Groups[2].Value.Trim());
    }

    [Theory]
    [InlineData("  smtp_password: smtpsecret")]
    [InlineData("  smtp_password: \"smtpsecret\"")]
    [InlineData("smtp_password: smtpsecret")]
    public void Regex_MatchesSmtpPasswordLine(string line)
    {
        var match = EncryptConfigCommand.PasswordLineRegex.Match(line);
        Assert.True(match.Success, $"Expected match for: {line}");
        Assert.Equal("smtpsecret", match.Groups[2].Value.Trim());
    }

    [Theory]
    [InlineData("  password: \"ENC:abc123\"")]
    [InlineData("  password: ENC:abc123")]
    [InlineData("  smtp_password: \"ENC:xyz789\"")]
    [InlineData("  smtp_password: ENC:xyz789")]
    public void Regex_SkipsAlreadyObfuscatedValues(string line)
    {
        var match = EncryptConfigCommand.PasswordLineRegex.Match(line);
        Assert.False(match.Success, $"Expected no match for already-obfuscated: {line}");
    }

    [Theory]
    [InlineData("  username: someone")]
    [InlineData("  some_password_field: value")]
    [InlineData("  notapassword: value")]
    public void Regex_DoesNotMatchUnrelatedKeys(string line)
    {
        var match = EncryptConfigCommand.PasswordLineRegex.Match(line);
        Assert.False(match.Success, $"Expected no match for unrelated key: {line}");
    }
}
