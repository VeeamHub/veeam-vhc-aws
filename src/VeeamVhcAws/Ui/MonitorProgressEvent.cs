using VeeamVhcAws.Core.Models;

namespace VeeamVhcAws.Ui;

public enum MonitorProgressStatus
{
    Queued,
    Running,
    Completed,
    Failed,
}

/// <summary>
/// Progress event emitted by MonitorRunner during execution.
/// Consumed by LiveMonitorView to update spinner states.
/// </summary>
public class MonitorProgressEvent
{
    public string Server { get; init; } = "";
    public string Monitor { get; init; } = "";
    public MonitorProgressStatus Status { get; init; }
    public MonitorResult? Result { get; init; }
}
