using VhcMonitor.Core.Models;
using VhcMonitor.Core.Patterns;
using VhcMonitor.Infrastructure;

namespace VhcMonitor.Monitors;

public interface IMonitor
{
    MonitorType Type { get; }
    IReadOnlyList<string> RequiredConnections { get; }
    MonitorResult Run(ServerContext serverContext, PatternEngine? patternEngine);
}
