using VeeamVhcAws.Core.Config;
using VeeamVhcAws.Core.Logging;
using VeeamVhcAws.Core.Output;
using Xunit;

namespace VeeamVhcAws.Tests.Core;

[Collection("console")]
public class ConfigPathJailTests : IDisposable
{
    private readonly string _dir;
    private readonly string _outside;
    private readonly string _configPath;

    public ConfigPathJailTests()
    {
        var root = Path.Combine(Path.GetTempPath(), "vhc-jail-" + Guid.NewGuid().ToString("N"));
        _dir = Path.Combine(root, "config");
        _outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(_dir);
        Directory.CreateDirectory(_outside);
        _configPath = Path.Combine(_dir, "veeam-vhc-aws.yaml");
        File.WriteAllText(_configPath, "global: {}\n");
    }

    public void Dispose()
    {
        var root = Path.GetDirectoryName(_dir)!;
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    [Fact]
    public void RelativePath_StaysNextToTheConfig()
    {
        var resolved = ConfigPathJail.Resolve(_configPath, "./veeam-vhc-aws-state.json");
        Assert.Equal(Path.Combine(_dir, "veeam-vhc-aws-state.json"), resolved);
    }

    [Fact]
    public void ParentSegment_IsRejected()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ConfigPathJail.Resolve(_configPath, "../outside/state.json"));
        Assert.Equal("Path is outside the config directory.", ex.Message);
    }

    [Fact]
    public void AbsolutePathInsideTheConfigDirectory_IsKept()
    {
        var inside = Path.Combine(_dir, "custom", "state.json");
        var resolved = ConfigPathJail.Resolve(_configPath, inside);
        Assert.Equal(Path.GetFullPath(inside), resolved);
    }

    [Fact]
    public void AbsolutePathOutsideTheConfigDirectory_IsRejected()
    {
        var outside = Path.Combine(_outside, "state.json");
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ConfigPathJail.Resolve(_configPath, outside));
        Assert.Equal("Path is outside the config directory.", ex.Message);
    }

    [Fact]
    public void SymlinkEscape_IsRejected()
    {
        var link = Path.Combine(_dir, "escape");
        Directory.CreateSymbolicLink(link, _outside);
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ConfigPathJail.Resolve(_configPath, Path.Combine(link, "state.json")));
        Assert.Equal("Path is outside the config directory.", ex.Message);
        Assert.False(File.Exists(Path.Combine(_outside, "state.json")));
    }

    [Fact]
    public void StateFile_UnderDataDirectory_IsAccepted_AndAPathOutsideBothRoots_IsRejected()
    {
        var dataDir = Path.Combine(Path.GetDirectoryName(_dir)!, "data");
        Directory.CreateDirectory(dataDir);
        var stateFile = Path.Combine(dataDir, "veeam-vhc-aws-state.json");
        File.WriteAllText(_configPath,
            "global:\n  data_directory: '" + dataDir + "'\n  state_file: '" + stateFile + "'\n");

        Assert.Equal(Path.GetFullPath(stateFile), ConfigLoader.ResolveStateFile(_configPath));

        var outside = Path.Combine(_outside, "state.json");
        File.WriteAllText(_configPath,
            "global:\n  data_directory: '" + dataDir + "'\n  state_file: '" + outside + "'\n");
        var rejected = Assert.Throws<InvalidOperationException>(() => ConfigLoader.ResolveStateFile(_configPath));
        Assert.Equal("Path is outside the config directory.", rejected.Message);

        File.WriteAllText(_configPath,
            "global:\n  data_directory: '" + dataDir + "'\n  state_file: '../data/veeam-vhc-aws-state.json'\n");
        var relative = Assert.Throws<InvalidOperationException>(() => ConfigLoader.ResolveStateFile(_configPath));
        Assert.Equal("Path is outside the config directory.", relative.Message);
    }

    [Fact]
    public void StateFile_StillResolvesBesideTheConfig_AndRejectsAnEscape()
    {
        File.WriteAllText(_configPath, "global:\n  state_file: ./veeam-vhc-aws-state.json\n");
        Assert.Equal(
            Path.Combine(_dir, "veeam-vhc-aws-state.json"),
            ConfigLoader.ResolveStateFile(_configPath));

        File.WriteAllText(_configPath, "global:\n  state_file: ../outside/state.json\n");
        var ex = Assert.Throws<InvalidOperationException>(() => ConfigLoader.ResolveStateFile(_configPath));
        Assert.Equal("Path is outside the config directory.", ex.Message);
    }

    [Fact]
    public void LogFile_OutsideTheConfigDirectory_IsRefused()
    {
        var outside = Path.Combine(_outside, "veeam-vhc-aws.log");
        var config = new Dictionary<string, object>
        {
            ["global"] = new Dictionary<string, object>
            {
                ["logging"] = new Dictionary<string, object>
                {
                    ["file"] = outside,
                    ["console"] = false,
                },
            },
        };

        var ex = Assert.Throws<InvalidOperationException>(() => LoggingSetup.Setup(config, _configPath));
        Assert.Equal("Path is outside the config directory.", ex.Message);
        Assert.False(File.Exists(outside));
    }

    [Fact]
    public void JsonFile_OutsideTheConfigDirectory_IsRefused()
    {
        var outside = Path.Combine(_outside, "out.json");
        var ex = Assert.Throws<InvalidOperationException>(() =>
            OutputHandlerFactory.CreateHandlers(new Dictionary<string, object>
            {
                ["output"] = new List<object>
                {
                    new Dictionary<string, object>
                    {
                        ["type"] = "json_file",
                        ["path"] = outside,
                    },
                },
            }, _configPath));
        Assert.Equal("Path is outside the config directory.", ex.Message);
        Assert.False(File.Exists(outside));
    }
}
