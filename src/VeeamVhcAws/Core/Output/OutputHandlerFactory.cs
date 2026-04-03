using VeeamVhcAws.Core.Config;
using VeeamVhcAws.Outputs;

namespace VeeamVhcAws.Core.Output;

public static class OutputHandlerFactory
{
    private static readonly Dictionary<string, Func<Dictionary<string, object>, IOutputHandler>> Registry = new()
    {
        ["json_stdout"] = _ => new JsonStdoutHandler(),
        ["json_file"] = cfg => new JsonFileHandler(
            cfg.Get("path", "/var/log/veeam-vhc-aws.json"),
            cfg.Get("rotate", false),
            cfg.Get("max_files", 30)),
        ["webhook"] = cfg => new WebhookHandler(
            cfg.Get<string>("url", ""),
            cfg.Get("template", "generic"),
            cfg.Get("min_severity", "ok"),
            cfg.Get("deduplicate", true)),
        ["prometheus"] = cfg => new PrometheusHandler(
            cfg.Get("mode", "pushgateway"),
            cfg.Get<string?>("url", null),
            cfg.Get("job", "veeam_vhc_aws"),
            cfg.Get("port", 9100)),
        ["email"] = cfg => new EmailHandler(
            cfg.Get("smtp_host", ""),
            cfg.Get("smtp_port", 587),
            cfg.Get("from_addr", ""),
            ParseToAddrs(cfg),
            cfg.Get("min_severity", "critical"),
            cfg.Get("smtp_username", ""),
            cfg.Get("smtp_password", ""),
            cfg.Get("use_tls", true)),
    };

    private static List<string> ParseToAddrs(Dictionary<string, object> cfg)
    {
        if (cfg.TryGetValue("to_addrs", out var val))
        {
            if (val is string s) return new List<string> { s };
            if (val is List<object> list) return list.Select(x => x.ToString() ?? "").ToList();
            if (val is List<string> sList) return sList;
        }
        return new List<string>();
    }

    public static List<IOutputHandler> CreateHandlers(Dictionary<string, object> config)
    {
        var outputConfigs = config.GetListOfSections("output");
        if (outputConfigs.Count == 0)
            outputConfigs = new List<Dictionary<string, object>>
            {
                new() { ["type"] = "json_stdout" }
            };

        var handlers = new List<IOutputHandler>();
        foreach (var entry in outputConfigs)
        {
            var handlerType = entry.Get("type", "json_stdout");
            if (!Registry.TryGetValue(handlerType, out var factory))
                throw new InvalidOperationException($"Unknown output handler type: {handlerType}");

            handlers.Add(factory(entry));
        }
        return handlers;
    }
}
