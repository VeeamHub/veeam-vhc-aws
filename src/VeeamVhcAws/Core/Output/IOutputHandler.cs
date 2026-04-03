using VeeamVhcAws.Core.Models;

namespace VeeamVhcAws.Core.Output;

public interface IOutputHandler
{
    void Emit(IReadOnlyList<MonitorResult> results);
}
