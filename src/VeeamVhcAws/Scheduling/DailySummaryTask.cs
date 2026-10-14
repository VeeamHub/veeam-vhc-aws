namespace VeeamVhcAws.Scheduling;

public interface IDailySummaryTaskHost
{
    bool IsWindows { get; }
    bool Exists();
    void Register(string configPath, string timeOfDay);
    void Unregister();
}

public static class DailySummaryTaskSync
{
    public const string TaskName = "Veeam VHC AWS Daily Summary";
    public const string DefaultTime = "08:00";

    public static string Apply(bool enabled, IDailySummaryTaskHost host, string configPath, string? timeOfDay)
    {
        if (!host.IsWindows)
            return "skipped";

        var exists = host.Exists();
        if (enabled && !exists)
        {
            host.Register(configPath, string.IsNullOrWhiteSpace(timeOfDay) ? DefaultTime : timeOfDay.Trim());
            return "registered";
        }
        if (!enabled && exists)
        {
            host.Unregister();
            return "unregistered";
        }
        return "unchanged";
    }
}

public static class DailySummaryTasks
{
    public static IDailySummaryTaskHost CreateDefault() =>
        OperatingSystem.IsWindows()
            ? new WindowsDailySummaryTaskHost()
            : new NoopDailySummaryTaskHost();
}

public sealed class NoopDailySummaryTaskHost : IDailySummaryTaskHost
{
    public bool IsWindows => false;
    public bool Exists() => false;
    public void Register(string configPath, string timeOfDay) { }
    public void Unregister() { }
}
