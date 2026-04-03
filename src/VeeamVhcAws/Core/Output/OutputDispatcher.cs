using VeeamVhcAws.Core.Models;

namespace VeeamVhcAws.Core.Output;

public class OutputDispatcher : IOutputHandler
{
    private readonly List<IOutputHandler> _handlers;

    public OutputDispatcher(List<IOutputHandler> handlers)
    {
        _handlers = handlers;
    }

    public void Emit(IReadOnlyList<MonitorResult> results)
    {
        foreach (var handler in _handlers)
        {
            handler.Emit(results);
        }
    }
}
