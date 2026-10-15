using System.Globalization;
using VeeamVhcAws.Core.Config;
using VeeamVhcAws.Ui;

namespace VeeamVhcAws.Web.Services;

public record ServerView(string Name, string Type, string Url, string Username, bool VerifySsl, string ApiVersion);

public record OutputView(string Type, Dictionary<string, object?> Fields, string? Id = null);

public record ConfigSnapshot(
    string ConfigPath,
    bool ConfigExists,
    IReadOnlyList<ServerView> Servers,
    IReadOnlyList<OutputView> Outputs,
    Dictionary<string, object> RepoHealth,
    Dictionary<string, object> Retention,
    Dictionary<string, object> WorkerHealth,
    Dictionary<string, object> Global,
    Dictionary<string, object> DailySummary);

public record ServerEdit(string Name, string Type, string Url, string Username, string Password, bool PasswordChanged, bool VerifySsl, string ApiVersion);

public record OutputEdit(string Type, Dictionary<string, object?> Fields, IReadOnlySet<string> ChangedSecretKeys, string? Id = null);

public class ConfigService
{
    public const string UrlChangeRequiresPassword = "A server URL change requires a new password.";

    private readonly string _configPath;
    private readonly object _saveLock = new();

    public ConfigService(WebHostOptions options)
    {
        _configPath = options.ConfigPath;
    }

    public string ConfigPath => _configPath;

    public ConfigSnapshot Load()
    {
        if (!File.Exists(_configPath))
            return new ConfigSnapshot(_configPath, false, [], [], new(), new(), new(), new(), new());

        var cfg = ConfigLoader.LoadConfig(_configPath);

        var servers = cfg.GetListOfSections("servers")
            .Select(s => new ServerView(
                Name: s.Get("name", ""),
                Type: s.Get("type", "vbr"),
                Url: s.Get("url", ""),
                Username: s.Get("username", ""),
                VerifySsl: s.Get("verify_ssl", true),
                ApiVersion: s.Get("api_version", "")))
            .ToList();

        var outputs = cfg.GetListOfSections("output")
            .Select((o, index) =>
            {
                var fields = new Dictionary<string, object?>();
                var outputType = o.Get("type", "unknown");
                foreach (var (k, v) in o)
                    if (k != "type") fields[k] = MaskSecret(outputType, k, v);
                return new OutputView(o.Get("type", "unknown"), fields, LoadedOutputId(index));
            })
            .ToList();

        return new ConfigSnapshot(
            ConfigPath: _configPath,
            ConfigExists: true,
            Servers: servers,
            Outputs: outputs,
            RepoHealth: cfg.GetSection("repo_health"),
            Retention: cfg.GetSection("retention"),
            WorkerHealth: cfg.GetSection("worker_health"),
            Global: cfg.GetSection("global"),
            DailySummary: cfg.GetSection("daily_summary"));
    }

    public void SaveServers(IReadOnlyList<ServerEdit> servers)
    {
        lock (_saveLock)
        {
            var existing = File.Exists(_configPath)
                ? ConfigLoader.LoadConfig(_configPath)
                : new Dictionary<string, object>();

            var existingByName = existing.GetListOfSections("servers")
                .ToDictionary(s => s.Get("name", ""), s => s, StringComparer.OrdinalIgnoreCase);

            var newList = new List<object>();
            foreach (var s in servers)
            {
                if (!s.PasswordChanged &&
                    existingByName.TryGetValue(s.Name, out var priorForUrl) &&
                    !string.Equals(priorForUrl.Get("url", ""), s.Url, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(UrlChangeRequiresPassword);
                }

                string passwordOnDisk;
                if (s.PasswordChanged)
                {
                    passwordOnDisk = string.IsNullOrEmpty(s.Password)
                        ? ""
                        : PasswordObfuscator.Obfuscate(s.Password);
                }
                else
                {
                    passwordOnDisk = existingByName.TryGetValue(s.Name, out var prior)
                        ? prior.Get("password", "")
                        : "";
                }

                var entry = new Dictionary<string, object>
                {
                    ["name"] = s.Name,
                    ["type"] = s.Type,
                    ["url"] = s.Url,
                    ["username"] = s.Username,
                    ["password"] = passwordOnDisk,
                    ["verify_ssl"] = s.VerifySsl,
                };
                if (!string.IsNullOrEmpty(s.ApiVersion))
                    entry["api_version"] = s.ApiVersion;
                newList.Add(entry);
            }

            existing["servers"] = newList;
            WriteYaml(existing);
        }
    }

    public void SaveOutputs(IReadOnlyList<OutputEdit> outputs)
    {
        lock (_saveLock)
        {
            var existing = File.Exists(_configPath)
                ? ConfigLoader.LoadConfig(_configPath)
                : new Dictionary<string, object>();

            var existingList = existing.GetListOfSections("output");
            var priorByLoadId = new Dictionary<string, Dictionary<string, object>>(existingList.Count);
            for (int index = 0; index < existingList.Count; index++)
                priorByLoadId[LoadedOutputId(index)] = existingList[index];

            var newList = new List<object>();
            foreach (var o in outputs)
            {
                priorByLoadId.TryGetValue(o.Id ?? "", out var prior);
                var entry = new Dictionary<string, object> { ["type"] = o.Type };
                foreach (var (k, v) in o.Fields)
                {
                    if (v is null) continue;

                    if (o.Type.Equals("webhook", StringComparison.OrdinalIgnoreCase) &&
                        k.Equals("url", StringComparison.OrdinalIgnoreCase) &&
                        v is string submittedUrl && submittedUrl == "********")
                    {
                        entry[k] = WebhookUrlFor(prior);
                    }
                    else if (IsSecretKey(k))
                    {
                        if (o.ChangedSecretKeys.Contains(k))
                        {
                            var pw = v as string ?? "";
                            entry[k] = string.IsNullOrEmpty(pw) ? "" : PasswordObfuscator.Obfuscate(pw);
                        }
                        else
                        {
                            entry[k] = prior?.Get(k, "") ?? "";
                        }
                    }
                    else
                    {
                        entry[k] = v;
                    }
                }
                if (prior != null &&
                    string.Equals(prior.Get("type", ""), o.Type, StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var (k, v) in prior)
                    {
                        if (!entry.ContainsKey(k))
                            entry[k] = v;
                    }
                }
                newList.Add(entry);
            }

            existing["output"] = newList;
            WriteYaml(existing);
        }
    }

    internal static Dictionary<string, object> OverlaySection(
        Dictionary<string, object> existing,
        Dictionary<string, object> updates)
    {
        var result = new Dictionary<string, object>(existing);
        foreach (var (key, value) in updates)
        {
            if (value is Dictionary<string, object> updateMap &&
                result.TryGetValue(key, out var current) &&
                current is Dictionary<string, object> currentMap)
            {
                result[key] = OverlaySection(currentMap, updateMap);
            }
            else
            {
                result[key] = value;
            }
        }
        return result;
    }

    public void SaveMonitorSection(string sectionName, Dictionary<string, object> section)
    {
        lock (_saveLock)
        {
            var existing = File.Exists(_configPath)
                ? ConfigLoader.LoadConfig(_configPath)
                : new Dictionary<string, object>();
            existing[sectionName] = section;
            WriteYaml(existing);
        }
    }

    private const string LoadedOutputPrefix = "loaded:";

    private static string LoadedOutputId(int index) =>
        LoadedOutputPrefix + index.ToString(CultureInfo.InvariantCulture);

    private static string WebhookUrlFor(Dictionary<string, object>? prior)
    {
        if (prior == null)
            return "";
        if (!prior.Get("type", "").Equals("webhook", StringComparison.OrdinalIgnoreCase))
            return "";
        return prior.Get("url", "");
    }

    private static bool IsSecretKey(string key) =>
        key.Contains("password", StringComparison.OrdinalIgnoreCase) ||
        key.Contains("token", StringComparison.OrdinalIgnoreCase) ||
        key.Contains("secret", StringComparison.OrdinalIgnoreCase);

    private void WriteYaml(Dictionary<string, object> config)
    {
        var yaml = ConfigBuilder.ToYaml(config);
        var dir = Path.GetDirectoryName(_configPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var tmp = _configPath + ".tmp";
        File.WriteAllText(tmp, yaml);
        File.Move(tmp, _configPath, overwrite: true);
    }

    private static object? MaskSecret(string outputType, string key, object? value)
    {
        if (value is null) return null;
        var maskWebhook = outputType.Equals("webhook", StringComparison.OrdinalIgnoreCase) &&
            key.Equals("url", StringComparison.OrdinalIgnoreCase);
        if (maskWebhook ||
            key.Contains("password", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("token", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("secret", StringComparison.OrdinalIgnoreCase))
            return string.IsNullOrEmpty(value as string) ? "" : "********";
        return value;
    }
}
