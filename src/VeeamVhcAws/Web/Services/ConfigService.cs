using VeeamVhcAws.Core.Config;
using VeeamVhcAws.Ui;

namespace VeeamVhcAws.Web.Services;

public record ServerView(string Name, string Type, string Url, string Username, bool VerifySsl, string ApiVersion);

public record OutputView(string Type, Dictionary<string, object?> Fields);

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

public record OutputEdit(string Type, Dictionary<string, object?> Fields, IReadOnlySet<string> ChangedSecretKeys);

public class ConfigService
{
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
            .Select(o =>
            {
                var fields = new Dictionary<string, object?>();
                foreach (var (k, v) in o)
                    if (k != "type") fields[k] = MaskSecret(k, v);
                return new OutputView(o.Get("type", "unknown"), fields);
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

    /// <summary>
    /// Replace the servers section with the provided list. Preserves password obfuscation:
    /// if <c>PasswordChanged</c> is false, the existing on-disk password is kept verbatim.
    /// </summary>
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

            var newList = new List<object>();
            for (int i = 0; i < outputs.Count; i++)
            {
                var o = outputs[i];
                var entry = new Dictionary<string, object> { ["type"] = o.Type };
                foreach (var (k, v) in o.Fields)
                {
                    if (v is null) continue;

                    if (IsSecretKey(k))
                    {
                        if (o.ChangedSecretKeys.Contains(k))
                        {
                            var pw = v as string ?? "";
                            entry[k] = string.IsNullOrEmpty(pw) ? "" : PasswordObfuscator.Obfuscate(pw);
                        }
                        else
                        {
                            entry[k] = i < existingList.Count ? existingList[i].Get(k, "") : "";
                        }
                    }
                    else
                    {
                        entry[k] = v;
                    }
                }
                newList.Add(entry);
            }

            existing["output"] = newList;
            WriteYaml(existing);
        }
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

    private static object? MaskSecret(string key, object? value)
    {
        if (value is null) return null;
        if (key.Contains("password", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("token", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("secret", StringComparison.OrdinalIgnoreCase))
            return string.IsNullOrEmpty(value as string) ? "" : "********";
        return value;
    }
}
