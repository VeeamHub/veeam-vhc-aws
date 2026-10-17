using NSubstitute;
using Xunit;
using VeeamVhcAws.Core.Clients;
using VeeamVhcAws.Core.State;
using VeeamVhcAws.Infrastructure;

namespace VeeamVhcAws.Tests.Infrastructure;

public class ParallelLastSuccessTests
{
    [Fact]
    public void ParallelServers_RecordLastSuccessOnlyAfterEveryMonitorReturns()
    {
        var statePath = Path.Combine(Path.GetTempPath(), $"vhc-parallel-{Guid.NewGuid()}.json");
        var sawWriteDuringRun = false;
        try
        {
            var healthy = new List<Dictionary<string, object>>
            {
                new()
                {
                    ["name"] = "repo-a",
                    ["capacityGB"] = 100d,
                    ["freeGB"] = 80d,
                    ["type"] = "WinLocal",
                }
            };

            IVbrClient FastClient()
            {
                var client = Substitute.For<IVbrClient>();
                client.GetRepositoryStates().Returns(healthy);
                client.GetSessions(Arg.Any<int>()).Returns(new List<Dictionary<string, object>>());
                client.GetScaleoutRepositories().Returns(new List<Dictionary<string, object>>());
                return client;
            }

            var slow = Substitute.For<IVbrClient>();
            slow.GetRepositoryStates().Returns(_ =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (sw.Elapsed < TimeSpan.FromMilliseconds(1000))
                {
                    if (File.Exists(statePath) && File.ReadAllText(statePath).Contains("lastSuccess", StringComparison.Ordinal))
                    {
                        sawWriteDuringRun = true;
                        break;
                    }
                    Thread.Sleep(20);
                }

                return healthy;
            });
            slow.GetSessions(Arg.Any<int>()).Returns(new List<Dictionary<string, object>>());
            slow.GetScaleoutRepositories().Returns(new List<Dictionary<string, object>>());

            var servers = new List<ServerContext>
            {
                new("s1", "vbr", VbrClient: FastClient()),
                new("s2", "vbr", VbrClient: FastClient()),
                new("s3", "vbr", VbrClient: slow),
            };
            var config = new Dictionary<string, object>
            {
                ["retention"] = new Dictionary<string, object> { ["enabled"] = false },
                ["worker_health"] = new Dictionary<string, object> { ["enabled"] = false },
            };

            var results = MonitorRunner.RunAllServers(servers, config, patternEngine: null, findingState: new FindingState(statePath));

            Assert.False(sawWriteDuringRun);
            Assert.Equal(3, results.Count);
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(statePath));
            Assert.Equal(3, doc.RootElement.GetProperty("lastSuccess").EnumerateObject().Count());
        }
        finally
        {
            if (File.Exists(statePath)) File.Delete(statePath);
            if (File.Exists(statePath + ".lock")) File.Delete(statePath + ".lock");
        }
    }
}
