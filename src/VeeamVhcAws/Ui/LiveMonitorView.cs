using Spectre.Console;
using VeeamVhcAws.Core.Models;

namespace VeeamVhcAws.Ui;

/// <summary>
/// Live progress table for <c>all</c>. With more than two servers the monitors report from
/// parallel worker threads, but a Spectre table is not thread-safe and the live display renders
/// it on its own thread. So <see cref="Report"/> only records the latest status per row under a
/// lock, and the live-view thread applies those statuses to the table before each refresh.
/// Nothing else mutates the table.
/// </summary>
public sealed class LiveMonitorView : IDisposable, IProgress<MonitorProgressEvent>
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(100);

    private readonly Table _table;
    private readonly Dictionary<string, int> _rowIndex;
    private readonly object _pendingGate = new();
    private readonly Dictionary<int, string> _pending = new();
    private readonly ManualResetEventSlim _disposed = new(false);
    private readonly ManualResetEventSlim _ctxReady = new(false);
    private Task? _liveTask;

    private LiveMonitorView(Table table, Dictionary<string, int> rowIndex)
    {
        _table = table;
        _rowIndex = rowIndex;
    }

    public static (LiveMonitorView View, Action<MonitorProgressEvent> Callback) Start(IEnumerable<(string Server, string Monitor)> plannedWork)
    {
        if (!TerminalCapabilities.IsInteractive)
        {
            var noop = new LiveMonitorView(new Table(), new Dictionary<string, int>());
            return (noop, _ => { });
        }

        var view = CreateDetached(plannedWork);
        view._liveTask = Task.Run(() =>
        {
            try
            {
                AnsiConsole.Live(view._table).Start(ctx =>
                {
                    view._ctxReady.Set();
                    while (!view._disposed.Wait(RefreshInterval))
                    {
                        if (view.ApplyPending())
                            ctx.Refresh();
                    }
                    view.ApplyPending();
                    ctx.Refresh();
                });
            }
            catch
            {
                view._ctxReady.Set(); // unblock caller even on failure
            }
        });

        view._ctxReady.Wait(TimeSpan.FromSeconds(1));
        return (view, view.Report);
    }

    /// <summary>Builds the view and its table without starting the live display.</summary>
    internal static LiveMonitorView CreateDetached(IEnumerable<(string Server, string Monitor)> plannedWork)
    {
        var table = new Table();
        table.AddColumn("[bold]Server[/]");
        table.AddColumn("[bold]Monitor[/]");
        table.AddColumn("[bold]Status[/]");
        table.Border(TableBorder.Rounded);
        table.Expand();

        var index = new Dictionary<string, int>();
        foreach (var (server, monitor) in plannedWork)
        {
            index[$"{server}::{monitor}"] = table.Rows.Count;
            table.AddRow(server.EscapeMarkup(), monitor.EscapeMarkup(), "[grey]Queued[/]");
        }

        return new LiveMonitorView(table, index);
    }

    /// <summary>Records a status from any thread. The table itself is not touched here.</summary>
    public void Report(MonitorProgressEvent evt)
    {
        if (!_rowIndex.TryGetValue($"{evt.Server}::{evt.Monitor}", out var row)) return;

        var statusMarkup = evt.Status switch
        {
            MonitorProgressStatus.Running   => "[yellow]Running...[/]",
            MonitorProgressStatus.Completed => evt.Result?.OverallSeverity == Severity.Ok
                ? "[green]Done — OK[/]"
                : $"[red]Done — {evt.Result?.OverallSeverity}[/]",
            MonitorProgressStatus.Failed    => "[red bold]Failed[/]",
            _                              => "[grey]Queued[/]",
        };

        lock (_pendingGate)
            _pending[row] = statusMarkup;
    }

    /// <summary>
    /// Applies recorded statuses to the table. Call only from the thread that renders it.
    /// Returns false when there was nothing to apply.
    /// </summary>
    internal bool ApplyPending()
    {
        KeyValuePair<int, string>[] updates;
        lock (_pendingGate)
        {
            if (_pending.Count == 0) return false;
            updates = _pending.ToArray();
            _pending.Clear();
        }

        foreach (var (row, markup) in updates)
            _table.UpdateCell(row, 2, markup);
        return true;
    }

    /// <summary>Renders the table as plain text. Same threading rule as <see cref="ApplyPending"/>.</summary>
    internal string RenderText()
    {
        var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.No,
            Out = new AnsiConsoleOutput(writer),
        });
        console.Profile.Width = 120;
        console.Write(_table);
        return writer.ToString();
    }

    public void Dispose()
    {
        _disposed.Set();
        _liveTask?.Wait(TimeSpan.FromSeconds(2));
        _disposed.Dispose();
        _ctxReady.Dispose();
    }
}
