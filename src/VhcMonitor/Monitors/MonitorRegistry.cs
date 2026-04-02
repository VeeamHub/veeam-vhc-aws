using VhcMonitor.Core.Config;

namespace VhcMonitor.Monitors;

public static class MonitorRegistry
{
    public static readonly Dictionary<string, Func<Dictionary<string, object>, IMonitor>> Monitors = new()
    {
        ["repo_health"] = config => new RepoHealthMonitor(config),
        ["retention"] = config => new RetentionMonitor(config),
        ["worker_health"] = config => new WorkerHealthMonitor(config),
    };

    public static readonly Dictionary<string, string> MonitorServerTypes = new()
    {
        ["repo_health"] = "vbr",
        ["retention"] = "vbr",
        ["worker_health"] = "vbaws",
    };
}
