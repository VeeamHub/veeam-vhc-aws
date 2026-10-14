using NSubstitute;
using VeeamVhcAws.Core.Clients;
using VeeamVhcAws.Core.Models;
using VeeamVhcAws.Infrastructure;
using Xunit;

namespace VeeamVhcAws.Tests.Infrastructure;

public class MonitorRunnerTests
{
    [Fact]
    public void PartialMonitorError_DoesNotSkipLaterMonitors()
    {
        var client = Substitute.For<IVbrClient>();
        client.GetRepositoryStates().Returns(new List<Dictionary<string, object>>
        {
            new()
            {
                ["name"] = "main",
                ["capacityGB"] = 100d,
                ["freeGB"] = 80d,
                ["type"] = "WinLocal",
            },
        });
        client.GetSessions(Arg.Any<int>()).Returns<List<Dictionary<string, object>>>(_ => throw new InvalidOperationException("sessions down"));
        client.GetScaleoutRepositories().Returns(new List<Dictionary<string, object>>());
        client.GetJobs().Returns(new List<Dictionary<string, object>>());
        client.GetBackups().Returns(new List<Dictionary<string, object>>());
        client.GetRestorePoints(Arg.Any<int>(), Arg.Any<int>()).Returns(new List<Dictionary<string, object>>());

        var ctx = new ServerContext("vbr1", "vbr", VbrClient: client);
        var config = new Dictionary<string, object>
        {
            ["repo_health"] = new Dictionary<string, object> { ["enabled"] = true },
            ["retention"] = new Dictionary<string, object> { ["enabled"] = true },
        };

        var results = MonitorRunner.RunAllServers(new List<ServerContext> { ctx }, config, patternEngine: null);

        Assert.Contains(results, r => r.Monitor == MonitorType.Retention);
    }
}
