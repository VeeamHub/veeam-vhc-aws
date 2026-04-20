using Serilog;
using VeeamVhcAws.Core.Auth;
using VeeamVhcAws.Core.Clients;
using VeeamVhcAws.Core.Config;

namespace VeeamVhcAws.Infrastructure;

public static class ServerContextBuilder
{
    private static readonly ILogger Logger = Log.ForContext(typeof(ServerContextBuilder));

    public static ServerContext BuildServerContext(Dictionary<string, object> serverCfg, Dictionary<string, object> globalCfg)
    {
        var name = serverCfg.Get<string>("name", "");
        var serverType = serverCfg.Get("type", "vbr").ToLowerInvariant();
        var url = serverCfg.Get("url", "");
        var username = serverCfg.Get("username", "");
        var password = PasswordObfuscator.Deobfuscate(serverCfg.Get("password", ""));
        var verifySsl = serverCfg.Get("verify_ssl", true);
        var timeout = globalCfg.Get("timeout_seconds", 30);
        var retryCount = globalCfg.Get("retry_count", 2);
        var retryDelay = globalCfg.Get("retry_delay_seconds", 5);
        var pageSize = globalCfg.Get("page_size", 500);
        var maxPages = globalCfg.Get("max_pages", 100);

        IVbrClient? vbrClient = null;
        IVbawsClient? vbawsClient = null;

        if (serverType == "vbr")
        {
            var apiVersion = serverCfg.Get("api_version", "1.3-rev1");
            var auth = new VbrAuth(url, username, password, apiVersion, verifySsl);
            vbrClient = new VbrClient(url, auth, verifySsl, timeout, retryCount, retryDelay, apiVersion, pageSize, maxPages);
        }
        else if (serverType == "vbaws")
        {
            var auth = new VbawsAuth(url, username, password, verifySsl);
            vbawsClient = new VbawsClient(url, auth, verifySsl, timeout, retryCount, retryDelay);
        }

        return new ServerContext(name, serverType, vbrClient, vbawsClient, new EmClient());
    }

    public static List<ServerContext> BuildAllServers(Dictionary<string, object> config)
    {
        var serversCfg = config.GetListOfSections("servers");
        var globalCfg = config.GetSection("global");
        var contexts = new List<ServerContext>();

        foreach (var serverCfg in serversCfg)
        {
            var name = serverCfg.Get("name", "");
            var url = serverCfg.Get("url", "");
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(url))
            {
                Logger.Warning("Skipping server entry missing name or url");
                continue;
            }
            try
            {
                var ctx = BuildServerContext(serverCfg, globalCfg);
                contexts.Add(ctx);
                Logger.Debug("Server '{Name}' ({Type}) configured at {Url}", ctx.Name, ctx.ServerType, url);
            }
            catch (Exception e)
            {
                Logger.Error("Failed to configure server '{Name}': {Error}", name, e.Message);
            }
        }
        return contexts;
    }
}
