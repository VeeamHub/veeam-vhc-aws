using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using Microsoft.Extensions.Logging;

namespace VhcMonitor.Core.Config;

public static class ConfigLoader
{
    private static readonly Dictionary<string, Dictionary<string, string>> SingleServerEnvVars = new()
    {
        ["vbr"] = new()
        {
            ["VEEAM_VBR_URL"] = "url",
            ["VEEAM_VBR_USERNAME"] = "username",
            ["VEEAM_VBR_PASSWORD"] = "password",
            ["VEEAM_VBR_API_VERSION"] = "api_version",
        },
        ["vbaws"] = new()
        {
            ["VEEAM_VBAWS_URL"] = "url",
            ["VEEAM_VBAWS_USERNAME"] = "username",
            ["VEEAM_VBAWS_PASSWORD"] = "password",
        },
    };

    private static readonly Dictionary<string, object> Defaults = new()
    {
        ["global"] = new Dictionary<string, object>
        {
            ["log_level"] = "DEBUG",
            ["timeout_seconds"] = 30,
            ["retry_count"] = 2,
            ["retry_delay_seconds"] = 5,
            ["logging"] = new Dictionary<string, object>
            {
                ["level"] = "DEBUG",
                ["file"] = "./vhc-monitor.log",
                ["rotation_when"] = "midnight",
                ["rotation_interval"] = 1,
                ["rotation_keep"] = 30,
                ["console"] = true,
                ["disk_warning_mb"] = 500,
            },
        },
        ["repo_health"] = new Dictionary<string, object>
        {
            ["enabled"] = true,
            ["thresholds"] = new Dictionary<string, object>
            {
                ["free_space_warning_pct"] = 15,
                ["free_space_critical_pct"] = 5,
                ["rescan_on_unhealthy"] = true,
            },
            ["include_external_repos"] = true,
            ["check_external_maintenance"] = true,
            ["external_maintenance_lookback_hours"] = 48,
        },
        ["retention"] = new Dictionary<string, object>
        {
            ["enabled"] = true,
            ["thresholds"] = new Dictionary<string, object>
            {
                ["overage_multiplier"] = 1.5,
                ["max_age_multiplier"] = 1.5,
                ["orphan_detection"] = true,
            },
        },
        ["worker_health"] = new Dictionary<string, object>
        {
            ["enabled"] = true,
            ["lookback_hours"] = 24,
            ["thresholds"] = new Dictionary<string, object>
            {
                ["session_failure_rate_warning"] = 0.1,
                ["session_failure_rate_critical"] = 0.3,
                ["retention_session_max_failures"] = 1,
                ["zero_deleted_items_warning"] = true,
                ["recurring_failure_threshold"] = 3,
            },
        },
        ["output"] = new List<object>
        {
            new Dictionary<string, object> { ["type"] = "json_stdout" },
        },
    };

    public static string GetConfigPath(string? configArg = null)
    {
        if (!string.IsNullOrEmpty(configArg))
            return configArg;

        var envPath = Environment.GetEnvironmentVariable("VHC_MONITOR_CONFIG");
        if (!string.IsNullOrEmpty(envPath))
            return envPath;

        return "./vhc-monitor.yaml";
    }

    public static Dictionary<string, object> LoadConfig(string path)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();

        var yaml = File.ReadAllText(path);
        var raw = deserializer.Deserialize<Dictionary<string, object>>(yaml)
            ?? new Dictionary<string, object>();

        var config = DeepMerge(Defaults, raw);
        ApplyEnvServers(config);
        return config;
    }

    public static Dictionary<string, object> DeepMerge(
        Dictionary<string, object> baseDict,
        Dictionary<string, object> overlay)
    {
        var result = new Dictionary<string, object>(baseDict);
        foreach (var (key, value) in overlay)
        {
            if (result.TryGetValue(key, out var existing)
                && existing is Dictionary<string, object> existingDict
                && value is Dictionary<string, object> overlayDict)
            {
                result[key] = DeepMerge(existingDict, overlayDict);
            }
            else
            {
                result[key] = value;
            }
        }
        return result;
    }

    private static void ApplyEnvServers(Dictionary<string, object> config)
    {
        if (config.ContainsKey("servers"))
            return;

        var servers = new List<object>();
        foreach (var (serverType, envMap) in SingleServerEnvVars)
        {
            var serverCfg = new Dictionary<string, object>();
            foreach (var (envVar, key) in envMap)
            {
                var value = Environment.GetEnvironmentVariable(envVar);
                if (value != null)
                    serverCfg[key] = value;
            }

            if (serverCfg.TryGetValue("url", out var url) && url is string urlStr && !string.IsNullOrEmpty(urlStr))
            {
                serverCfg["name"] = $"env-{serverType}";
                serverCfg["type"] = serverType;
                servers.Add(serverCfg);
            }
        }

        if (servers.Count > 0)
            config["servers"] = servers;
    }
}
