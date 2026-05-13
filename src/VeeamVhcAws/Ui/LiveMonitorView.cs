using Spectre.Console;
using VeeamVhcAws.Core.Models;

namespace VeeamVhcAws.Ui;

public sealed class LiveMonitorView : IDisposable
{
    private readonly Table _table;
    private readonly Dictionary<string, int> _rowIndex = new();
    private readonly ManualResetEventSlim _disposed = new(false);
    private readonly ManualResetEventSlim _ctxReady = new(false);
    private Task? _liveTask;
    private LiveDisplayContext? _ctx;

    private LiveMonitorView(Table table)
    {
        _table = table;
    }

    public static (LiveMonitorView View, Action<MonitorProgressEvent> Callback) Start(IEnumerable<(string Server, string Monitor)> plannedWork)
    {
        if (!TerminalCapabilities.IsInteractive)
        {
            var noop = new LiveMonitorView(new Table());
            return (noop, _ => { });
        }

        var table = new Table();
        table.AddColumn("[bold]Server[/]");
        table.AddColumn("[bold]Monitor[/]");
        table.AddColumn("[bold]Status[/]");
        table.Border(TableBorder.Rounded);
        table.Expand();

        var workList = plannedWork.ToList();
        foreach (var (server, monitor) in workList)
            table.AddRow(server.EscapeMarkup(), monitor.EscapeMarkup(), "[grey]Queued[/]");

        var index = new Dictionary<string, int>();
        for (int i = 0; i < workList.Count; i++)
            index[$"{workList[i].Server}::{workList[i].Monitor}"] = i;

        var view = new LiveMonitorView(table);
        foreach (var (k, v) in index)
            view._rowIndex[k] = v;

        view._liveTask = Task.Run(() =>
        {
            try
            {
                AnsiConsole.Live(table).Start(ctx =>
                {
                    view._ctx = ctx;
                    view._ctxReady.Set();
                    view._disposed.Wait();
                });
            }
            catch
            {
                view._ctxReady.Set(); // unblock caller even on failure
            }
        });

        view._ctxReady.Wait(TimeSpan.FromSeconds(1));

        Action<MonitorProgressEvent> callback = evt =>
        {
            var key = $"{evt.Server}::{evt.Monitor}";
            if (!view._rowIndex.TryGetValue(key, out var row)) return;

            var statusMarkup = evt.Status switch
            {
                MonitorProgressStatus.Running   => "[yellow]Running...[/]",
                MonitorProgressStatus.Completed => evt.Result?.OverallSeverity == Severity.Ok
                    ? "[green]Done — OK[/]"
                    : $"[red]Done — {evt.Result?.OverallSeverity}[/]",
                MonitorProgressStatus.Failed    => "[red bold]Failed[/]",
                _                              => "[grey]Queued[/]",
            };

            table.UpdateCell(row, 2, statusMarkup);
            view._ctx?.Refresh();
        };

        return (view, callback);
    }

    public void Dispose()
    {
        _disposed.Set();
        _liveTask?.Wait(TimeSpan.FromSeconds(2));
        _disposed.Dispose();
        _ctxReady.Dispose();
    }
}
