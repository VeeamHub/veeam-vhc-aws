using VeeamVhcAws.Core.Models;
using VeeamVhcAws.Core.Patterns;
using VeeamVhcAws.Core.State;
using VeeamVhcAws.Infrastructure;

namespace VeeamVhcAws.Monitors;

public interface IMonitor
{
    MonitorType Type { get; }
    IReadOnlyList<string> RequiredConnections { get; }
    MonitorResult Run(ServerContext serverContext, PatternEngine? patternEngine, FindingState? findingState = null);
}
