using System.Text.Json;
using VhcMonitor.Core.Models;
using VhcMonitor.Core.Output;

namespace VhcMonitor.Outputs;

public class JsonStdoutHandler : IOutputHandler
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
    };

    public void Emit(IReadOnlyList<MonitorResult> results)
    {
        var target = Console.Out;
        if (target == null || target == TextWriter.Null)
            return;

        var output = results.Select(r => r.ToDictionary()).ToList();
        var json = JsonSerializer.Serialize(output, Options);
        target.WriteLine(json);
        target.Flush();
    }
}
