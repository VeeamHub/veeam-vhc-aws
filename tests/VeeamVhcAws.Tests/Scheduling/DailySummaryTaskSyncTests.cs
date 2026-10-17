using Xunit;
using VeeamVhcAws.Scheduling;

namespace VeeamVhcAws.Tests.Scheduling;

public class DailySummaryTaskSyncTests
{
    private sealed class RecordingHost : IDailySummaryTaskHost
    {
        public bool IsWindows { get; init; } = true;
        public bool ExistsValue { get; set; }
        public List<string> Calls { get; } = new();

        public bool Exists() => ExistsValue;

        public void Register(string configPath, string timeOfDay)
        {
            Calls.Add($"register {timeOfDay} {configPath}");
            ExistsValue = true;
        }

        public void Unregister()
        {
            Calls.Add("unregister");
            ExistsValue = false;
        }
    }

    [Fact]
    public void Apply_EnabledAndMissing_RegistersAtTheGivenTime()
    {
        var host = new RecordingHost();
        var outcome = DailySummaryTaskSync.Apply(true, host, @"C:\ProgramData\VHC\veeam-vhc-aws.yaml", "07:30");

        Assert.Equal("registered", outcome);
        Assert.Equal(new[] { @"register 07:30 C:\ProgramData\VHC\veeam-vhc-aws.yaml" }, host.Calls);
    }

    [Fact]
    public void Apply_DisabledAndPresent_Unregisters()
    {
        var host = new RecordingHost { ExistsValue = true };
        var outcome = DailySummaryTaskSync.Apply(false, host, @"C:\cfg.yaml", "08:00");

        Assert.Equal("unregistered", outcome);
        Assert.Equal(new[] { "unregister" }, host.Calls);
    }

    [Fact]
    public void Apply_EnabledAndAlreadyPresent_LeavesTheTask()
    {
        var host = new RecordingHost { ExistsValue = true };
        var outcome = DailySummaryTaskSync.Apply(true, host, @"C:\cfg.yaml", "08:00");

        Assert.Equal("unchanged", outcome);
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void Apply_OffWindows_DoesNotTouchTheScheduler()
    {
        var host = new RecordingHost { IsWindows = false, ExistsValue = true };
        var outcome = DailySummaryTaskSync.Apply(true, host, @"C:\cfg.yaml", "08:00");

        Assert.Equal("skipped", outcome);
        Assert.Empty(host.Calls);
    }
}
