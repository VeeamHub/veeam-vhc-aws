using VeeamVhcAws.Core.Models;
using VeeamVhcAws.Infrastructure;
using Xunit;

namespace VeeamVhcAws.Tests.Infrastructure;

public class CrossCorrelatorTests
{
    [Fact]
    public void WidespreadFailures_MatchAfterServerPrefix()
    {
        var results = new List<MonitorResult>
        {
            new(MonitorType.WorkerHealth, DateTime.UtcNow, 10, Severity.Warning,
                new List<Finding>
                {
                    new(Severity.Warning, "[prod] failed:policy-a", "timeout"),
                    new(Severity.Critical, "[prod] failed:policy-b", "timeout"),
                },
                server: "prod"),
        };

        var correlated = CrossCorrelator.Correlate(results);

        Assert.Contains(correlated, r =>
            r.Monitor == MonitorType.CrossCorrelation &&
            r.Findings.Any(f => f.Message.Contains("Widespread session failures", StringComparison.Ordinal)));
    }
}
