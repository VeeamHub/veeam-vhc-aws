using System.Text;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using Microsoft.Extensions.Logging;

namespace VeeamVhcAws.Core.Config;

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
                ["file"] = "./veeam-vhc-aws.log",
                ["rotation_when"] = "midnight",
                ["rotation_interval"] = 1,
                ["rotation_keep"] = 30,
                ["console"] = true,
                ["disk_warning_pct"] = 20,
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

        var envPath = Environment.GetEnvironmentVariable("VEEAM_VHC_AWS_CONFIG");
        if (!string.IsNullOrEmpty(envPath))
            return envPath;

        return "./veeam-vhc-aws.yaml";
    }

    public static Dictionary<string, object> LoadConfig(string path)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();

        var yaml = NormalizeBackslashes(File.ReadAllText(path));
        Dictionary<string, object> raw;
        try
        {
            raw = deserializer.Deserialize<Dictionary<string, object>>(yaml)
                ?? new Dictionary<string, object>();
        }
        catch (YamlException ex)
        {
            var lines = yaml.Split('\n');
            var line = ex.Start.Line > 0 && ex.Start.Line <= lines.Length
                ? $"\n  Line {ex.Start.Line}: {lines[ex.Start.Line - 1].Trim()}"
                : string.Empty;
            throw new InvalidOperationException(
                $"Config parse error in '{path}' — check for unescaped special characters in quoted values " +
                $"(use single quotes or escape backslashes as \\\\).{line}", ex);
        }

        var config = DeepMerge(Defaults, raw);
        ApplyEnvServers(config);
        return config;
    }

    /// <summary>
    /// Normalizes bare backslashes in YAML double-quoted strings so that
    /// Windows paths and complex passwords don't require manual escaping.
    /// YamlDotNet treats \u, \U, \x as Unicode/hex escapes; this doubles any
    /// backslash that doesn't form a valid YAML escape sequence.
    /// </summary>
    internal static string NormalizeBackslashes(string yaml)
    {
        var sb = new StringBuilder(yaml.Length);
        var i = 0;

        while (i < yaml.Length)
        {
            var c = yaml[i];

            if (c == '\'')
            {
                // Single-quoted scalar: no escape processing, just copy until closing '
                // The only special sequence is '' (escaped single quote).
                sb.Append(c);
                i++;
                while (i < yaml.Length)
                {
                    if (yaml[i] == '\'' && i + 1 < yaml.Length && yaml[i + 1] == '\'')
                    {
                        sb.Append("''");
                        i += 2;
                    }
                    else if (yaml[i] == '\'')
                    {
                        sb.Append(yaml[i++]);
                        break;
                    }
                    else
                    {
                        sb.Append(yaml[i++]);
                    }
                }
            }
            else if (c == '"')
            {
                // Double-quoted scalar: normalize invalid backslash escapes.
                sb.Append(c);
                i++;
                while (i < yaml.Length && yaml[i] != '"')
                {
                    if (yaml[i] == '\\' && i + 1 < yaml.Length)
                    {
                        if (IsValidYamlEscape(yaml, i))
                        {
                            sb.Append(yaml[i++]);
                            sb.Append(yaml[i++]);
                        }
                        else
                        {
                            sb.Append('\\');
                            sb.Append('\\');
                            i++;
                        }
                    }
                    else
                    {
                        sb.Append(yaml[i++]);
                    }
                }
                if (i < yaml.Length)
                    sb.Append(yaml[i++]); // closing "
            }
            else
            {
                sb.Append(yaml[i++]);
            }
        }

        return sb.ToString();
    }

    private static bool IsValidYamlEscape(string s, int pos)
    {
        if (pos + 1 >= s.Length) return false;
        return s[pos + 1] switch
        {
            '0' or 'a' or 'b' or 't' or '\t' or 'n' or 'v' or 'f'
                or 'r' or 'e' or '"' or '\\' or '/' or 'N' or '_'
                or 'L' or 'P' or '\n' => true,
            'x' => pos + 3 < s.Length && IsHex(s[pos + 2]) && IsHex(s[pos + 3]),
            'u' => pos + 5 < s.Length && IsHex(s, pos + 2, 4),
            'U' => pos + 9 < s.Length && IsHex(s, pos + 2, 8),
            _ => false
        };
    }

    private static bool IsHex(char c) =>
        c is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F');

    private static bool IsHex(string s, int start, int count)
    {
        for (var i = 0; i < count; i++)
            if (start + i >= s.Length || !IsHex(s[start + i])) return false;
        return true;
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
