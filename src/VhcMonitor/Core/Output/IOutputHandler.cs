using VhcMonitor.Core.Models;

namespace VhcMonitor.Core.Output;

public interface IOutputHandler
{
    void Emit(IReadOnlyList<MonitorResult> results);
}
