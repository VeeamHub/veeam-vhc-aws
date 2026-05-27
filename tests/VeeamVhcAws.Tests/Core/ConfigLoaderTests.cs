using Xunit;
using VeeamVhcAws.Core.Config;

namespace VeeamVhcAws.Tests.Core;

public class ConfigLoaderTests
{
    [Fact]
    public void DeepMergeOverlayWins()
    {
        var baseDict = new Dictionary<string, object>
        {
            ["a"] = 1,
            ["b"] = new Dictionary<string, object> { ["x"] = 10, ["y"] = 20 },
        };
        var overlay = new Dictionary<string, object>
        {
            ["a"] = 2,
            ["b"] = new Dictionary<string, object> { ["y"] = 30, ["z"] = 40 },
            ["c"] = 3,
        };

        var result = ConfigLoader.DeepMerge(baseDict, overlay);

        Assert.Equal(2, result["a"]);
        Assert.Equal(3, result["c"]);
        var b = (Dictionary<string, object>)result["b"];
        Assert.Equal(10, b["x"]);
        Assert.Equal(30, b["y"]);
        Assert.Equal(40, b["z"]);
    }

    [Fact]
    public void GetConfigPathUsesCliArg()
    {
        var result = ConfigLoader.GetConfigPath("/my/config.yaml");
        Assert.Equal("/my/config.yaml", result);
    }

    [Fact]
    public void GetConfigPathFallsToDefault()
    {
        // With no arg and no env var, should return an absolute path ending in veeam-vhc-aws.yaml
        var original = Environment.GetEnvironmentVariable("VEEAM_VHC_AWS_CONFIG");
        try
        {
            Environment.SetEnvironmentVariable("VEEAM_VHC_AWS_CONFIG", null);
            var result = ConfigLoader.GetConfigPath(null);
            Assert.True(Path.IsPathRooted(result), "default path should be absolute");
            Assert.Equal("veeam-vhc-aws.yaml", Path.GetFileName(result));
        }
        finally
        {
            Environment.SetEnvironmentVariable("VHC_MONITOR_CONFIG", original);
        }
    }

    [Fact]
    public void GetConfigPathUsesEnvVar()
    {
        var original = Environment.GetEnvironmentVariable("VEEAM_VHC_AWS_CONFIG");
        try
        {
            Environment.SetEnvironmentVariable("VEEAM_VHC_AWS_CONFIG", "/env/config.yaml");
            var result = ConfigLoader.GetConfigPath(null);
            Assert.Equal("/env/config.yaml", result);
        }
        finally
        {
            Environment.SetEnvironmentVariable("VHC_MONITOR_CONFIG", original);
        }
    }

    [Fact]
    public void ConfigExtensionsGetDefault()
    {
        var dict = new Dictionary<string, object> { ["key"] = "value" };
        Assert.Equal("value", dict.Get("key", "default"));
        Assert.Equal("default", dict.Get("missing", "default"));
    }

    [Fact]
    public void ConfigExtensionsGetSection()
    {
        var dict = new Dictionary<string, object>
        {
            ["nested"] = new Dictionary<string, object> { ["a"] = 1 }
        };
        var section = dict.GetSection("nested");
        Assert.Equal(1, section["a"]);

        var empty = dict.GetSection("missing");
        Assert.Empty(empty);
    }

    [Theory]
    [InlineData(@"username: ""DOMAIN\Username""", @"username: ""DOMAIN\\Username""")]
    [InlineData(@"password: ""P@ss\word!""", @"password: ""P@ss\\word!""")]
    // \U (Users) and \D (Desktop) are invalid YAML escapes → doubled.
    // \a (adam) IS a valid YAML escape (bell char) → left as-is per spec.
    [InlineData("path: \"C:\\Users\\adam\\Desktop\"", "path: \"C:\\\\Users\\adam\\\\Desktop\"")]
    public void NormalizeBackslashes_EscapesBareBackslashInDoubleQuoted(string input, string expected)
    {
        var result = ConfigLoader.NormalizeBackslashes(input);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("key: \"already\\\\escaped\"")]
    [InlineData("key: \"newline\\nhere\"")]
    [InlineData("key: \"tab\\there\"")]
    public void NormalizeBackslashes_LeavesValidEscapesAlone(string input)
    {
        var result = ConfigLoader.NormalizeBackslashes(input);
        Assert.Equal(input, result);
    }

    [Fact]
    public void NormalizeBackslashes_IgnoresSingleQuotedStrings()
    {
        var input = @"username: 'DOMAIN\Username'";
        var result = ConfigLoader.NormalizeBackslashes(input);
        Assert.Equal(input, result);
    }

    [Fact]
    public void NormalizeBackslashes_ParsesWindowsDomainCredentials()
    {
        var yaml = "servers:\n  - username: \"DOMAIN\\\\adam\"\n    password: \"P@ss\\\\w0rd!\"";
        // Should not throw
        var result = ConfigLoader.NormalizeBackslashes(yaml);
        Assert.Contains("DOMAIN", result);
    }

    [Fact]
    public void NormalizeBackslashes_HandlesValidUnicodeEscape()
    {
        // \u0041 is valid (4 hex digits) — should pass through unchanged
        var input = "key: \"\\u0041\"";
        var result = ConfigLoader.NormalizeBackslashes(input);
        Assert.Equal(input, result);
    }

    [Fact]
    public void NormalizeBackslashes_FixesInvalidUnicodeEscape()
    {
        // \Users — \U not followed by 8 hex digits → should be doubled
        var input = "key: \"\\Users\"";
        var result = ConfigLoader.NormalizeBackslashes(input);
        Assert.Equal("key: \"\\\\Users\"", result);
    }

    [Fact]
    public void GetListOfStringsReturnsStringsFromList()
    {
        var dict = new Dictionary<string, object>
        {
            ["items"] = new List<object> { "alpha", "beta", "gamma" }
        };
        var result = dict.GetListOfStrings("items");
        Assert.Equal(3, result.Count);
        Assert.Equal("alpha", result[0]);
        Assert.Equal("beta", result[1]);
        Assert.Equal("gamma", result[2]);
    }

    [Fact]
    public void GetListOfStringsReturnsEmptyForMissingKey()
    {
        var dict = new Dictionary<string, object>();
        var result = dict.GetListOfStrings("missing");
        Assert.Empty(result);
    }

    [Fact]
    public void GetListOfStringsHandlesNonListValue()
    {
        var dict = new Dictionary<string, object> { ["items"] = "not a list" };
        var result = dict.GetListOfStrings("items");
        Assert.Empty(result);
    }
}
