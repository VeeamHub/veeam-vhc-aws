using System.Text.Json;
using Serilog;
using VeeamVhcAws.Core.Models;
using VeeamVhcAws.Core.Output;

namespace VeeamVhcAws.Outputs;

public class JsonStdoutHandler : IOutputHandler
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
    };

    public bool WarnWhenDiscarded { get; init; }

    public void Emit(IReadOnlyList<MonitorResult> results)
    {
        var target = Console.Out;
        if (target == null || target == TextWriter.Null)
        {
            if (WarnWhenDiscarded)
                Log.Warning(StdoutOnlyOutput.Warning);
            return;
        }

        var output = results.Select(r => r.ToDictionary()).ToList();
        var json = JsonSerializer.Serialize(output, Options);
        target.WriteLine(json);
        target.Flush();
    }
}
