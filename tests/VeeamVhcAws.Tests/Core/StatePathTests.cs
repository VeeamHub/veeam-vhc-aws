using VeeamVhcAws.Web;
using VeeamVhcAws.Web.Services;
using Xunit;

namespace VeeamVhcAws.Tests.Core;

public class StatePathTests : IDisposable
{
    private readonly string _dir;

    public StatePathTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "vhc-state-path-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void RelativeStateFile_ResolvesNextToTheConfig()
    {
        var configPath = Path.Combine(_dir, "veeam-vhc-aws.yaml");
        File.WriteAllText(configPath, "global:\n  state_file: ./veeam-vhc-aws-state.json\n");

        var svc = new StateService(new WebHostOptions(configPath, "127.0.0.1", 0));

        Assert.Equal(Path.Combine(_dir, "veeam-vhc-aws-state.json"), svc.StateFilePath);
    }

    [Fact]
    public void MissingConfig_StillAnchorsTheDefaultStateFile()
    {
        var configPath = Path.Combine(_dir, "veeam-vhc-aws.yaml");
        var svc = new StateService(new WebHostOptions(configPath, "127.0.0.1", 0));

        Assert.Equal(Path.Combine(_dir, "veeam-vhc-aws-state.json"), svc.StateFilePath);
    }

    [Fact]
    public void AbsoluteStateFile_StaysAbsolute()
    {
        var configPath = Path.Combine(_dir, "veeam-vhc-aws.yaml");
        var absolute = Path.Combine(_dir, "custom", "state.json");
        File.WriteAllText(configPath, "global:\n  state_file: \"" + absolute.Replace("\\", "\\\\") + "\"\n");

        var svc = new StateService(new WebHostOptions(configPath, "127.0.0.1", 0));

        Assert.Equal(Path.GetFullPath(absolute), svc.StateFilePath);
    }
}
