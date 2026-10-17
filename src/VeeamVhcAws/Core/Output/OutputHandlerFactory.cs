using VeeamVhcAws.Core.Config;
using VeeamVhcAws.Outputs;

namespace VeeamVhcAws.Core.Output;

public static class OutputHandlerFactory
{
    private static readonly Dictionary<string, Func<Dictionary<string, object>, string, IOutputHandler>> Registry = new()
    {
        ["json_file"] = (cfg, configPath) => new JsonFileHandler(
            ConfigPathJail.Resolve(configPath, cfg.Get("path", "/var/log/veeam-vhc-aws.json")),
            cfg.Get("rotate", false),
            cfg.Get("max_files", 30)),
        ["webhook"] = (cfg, _) => new WebhookHandler(
            cfg.Get<string>("url", ""),
            cfg.Get("template", "generic"),
            cfg.Get("min_severity", "ok"),
            cfg.Get("deduplicate", true),
            cfg.Get("allow_loopback", false)),
        ["prometheus"] = (cfg, _) => new PrometheusHandler(
            cfg.Get("mode", "pushgateway"),
            cfg.Get<string?>("url", null),
            cfg.Get("job", "veeam_vhc_aws"),
            cfg.Get("port", 9100),
            cfg.Get("allow_loopback", false)),
        ["email"] = (cfg, _) => new EmailHandler(
            cfg.Get("smtp_host", ""),
            cfg.Get("smtp_port", 587),
            cfg.Get("from_addr", ""),
            ParseToAddrs(cfg),
            cfg.Get("min_severity", "critical"),
            cfg.Get("smtp_username", ""),
            PasswordObfuscator.Deobfuscate(cfg.Get("smtp_password", "")),
            cfg.Get("use_tls", true),
            cfg.Get("deduplicate", true)),
    };

    private static List<string> ParseToAddrs(Dictionary<string, object> cfg)
    {
        if (cfg.TryGetValue("to_addrs", out var val))
        {
            if (val is string s)
                return s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            if (val is List<object> list) return list.Select(x => x.ToString() ?? "").ToList();
            if (val is List<string> sList) return sList;
        }
        return new List<string>();
    }

    public static List<IOutputHandler> CreateHandlers(Dictionary<string, object> config, string configPath)
    {
        var outputConfigs = config.GetListOfSections("output");
        if (outputConfigs.Count == 0)
            outputConfigs = new List<Dictionary<string, object>>
            {
                new() { ["type"] = "json_stdout" }
            };

        var onlyStdout = outputConfigs.All(entry =>
            string.Equals(entry.Get("type", "json_stdout"), "json_stdout", StringComparison.OrdinalIgnoreCase));

        var handlers = new List<IOutputHandler>();
        foreach (var entry in outputConfigs)
        {
            var handlerType = entry.Get("type", "json_stdout");
            if (handlerType == "json_stdout")
            {
                handlers.Add(new JsonStdoutHandler { WarnWhenDiscarded = onlyStdout });
                continue;
            }
            if (!Registry.TryGetValue(handlerType, out var factory))
                throw new InvalidOperationException($"Unknown output handler type: {handlerType}");

            handlers.Add(factory(entry, configPath));
        }
        return handlers;
    }
}

public static class StdoutOnlyOutput
{
    public const string Warning =
        "JSON stdout is the only output handler. A scheduled task discards stdout, so this run will not deliver findings. Add email, a webhook, or a JSON file.";

    public static bool IsOnly(IEnumerable<string> types)
    {
        var list = types as IReadOnlyList<string> ?? types.ToList();
        return list.Count > 0 && list.All(type => string.Equals(type, "json_stdout", StringComparison.OrdinalIgnoreCase));
    }
}
