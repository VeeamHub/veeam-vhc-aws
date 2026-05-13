using Xunit;
using VeeamVhcAws.Ui;
using VeeamVhcAws.Core.Models;

namespace VeeamVhcAws.Tests.Ui;

/// <summary>
/// TDD tests for Theme severity style mappings.
/// </summary>
public class ThemeTests
{
    [Theory]
    [InlineData(Severity.Ok, "green")]
    [InlineData(Severity.Warning, "yellow")]
    [InlineData(Severity.Critical, "red bold")]
    [InlineData(Severity.Error, "red bold")]
    public void SevStyle_ContainsExpectedColor(Severity severity, string expectedColor)
    {
        var style = Theme.SevStyle(severity);
        Assert.Contains(expectedColor, style, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(Severity.Ok, "[OK]")]
    [InlineData(Severity.Warning, "[!]")]
    [InlineData(Severity.Critical, "[!!]")]
    [InlineData(Severity.Error, "[X]")]
    public void Icon_ReturnsExpectedText(Severity severity, string expectedIcon)
    {
        var icon = Theme.Icon(severity);
        Assert.Equal(expectedIcon, icon);
    }

    [Theory]
    [InlineData(Severity.Ok, "[OK]")]
    [InlineData(Severity.Warning, "[WARN]")]
    [InlineData(Severity.Critical, "[CRIT]")]
    [InlineData(Severity.Error, "[ERR]")]
    public void AsciiPrefix_ContainsExpectedTag(Severity severity, string expectedTag)
    {
        var prefix = Theme.AsciiPrefix(severity);
        Assert.Contains(expectedTag, prefix, StringComparison.Ordinal);
    }

    [Fact]
    public void AsciiPrefix_Ok_HasTrailingSpace_ForAlignment()
    {
        // [OK]   has trailing spaces to align with longer prefixes
        var prefix = Theme.AsciiPrefix(Severity.Ok);
        Assert.True(prefix.Length >= 6, $"[OK] prefix should be padded for alignment, got: '{prefix}'");
    }

    [Fact]
    public void AsciiPrefix_AllSeverities_SameLength()
    {
        var lengths = new[]
        {
            Theme.AsciiPrefix(Severity.Ok).Length,
            Theme.AsciiPrefix(Severity.Warning).Length,
            Theme.AsciiPrefix(Severity.Critical).Length,
            Theme.AsciiPrefix(Severity.Error).Length,
        };
        // All prefixes should be the same length for column alignment
        Assert.True(lengths.Distinct().Count() == 1,
            $"All prefixes must be same length for alignment. Lengths: {string.Join(", ", lengths)}");
    }

    [Fact]
    public void SevStyle_Ok_WrappedInBrackets()
    {
        var style = Theme.SevStyle(Severity.Ok);
        Assert.StartsWith("[", style);
        Assert.EndsWith("]", style);
    }

    [Fact]
    public void SevStyle_CriticalAndError_HaveSameStyle()
    {
        // Both critical and error are red bold
        var critical = Theme.SevStyle(Severity.Critical);
        var error = Theme.SevStyle(Severity.Error);
        Assert.Equal(critical, error);
    }

    [Fact]
    public void Icon_AllSeveritiesReturnNonEmpty()
    {
        foreach (Severity s in Enum.GetValues<Severity>())
        {
            var icon = Theme.Icon(s);
            Assert.False(string.IsNullOrEmpty(icon), $"Icon for {s} must not be empty");
        }
    }

    [Fact]
    public void SevStyle_AllSeveritiesReturnNonEmpty()
    {
        foreach (Severity s in Enum.GetValues<Severity>())
        {
            var style = Theme.SevStyle(s);
            Assert.False(string.IsNullOrEmpty(style), $"SevStyle for {s} must not be empty");
        }
    }
}
