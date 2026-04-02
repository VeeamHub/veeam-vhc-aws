using Xunit;
using VhcMonitor.Core.Config;

namespace VhcMonitor.Tests.Core;

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
        // With no arg and no env var, should return default
        var original = Environment.GetEnvironmentVariable("VHC_MONITOR_CONFIG");
        try
        {
            Environment.SetEnvironmentVariable("VHC_MONITOR_CONFIG", null);
            var result = ConfigLoader.GetConfigPath(null);
            Assert.Equal("./vhc-monitor.yaml", result);
        }
        finally
        {
            Environment.SetEnvironmentVariable("VHC_MONITOR_CONFIG", original);
        }
    }

    [Fact]
    public void GetConfigPathUsesEnvVar()
    {
        var original = Environment.GetEnvironmentVariable("VHC_MONITOR_CONFIG");
        try
        {
            Environment.SetEnvironmentVariable("VHC_MONITOR_CONFIG", "/env/config.yaml");
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
}
