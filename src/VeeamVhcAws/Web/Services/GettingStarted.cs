namespace VeeamVhcAws.Web.Services;

public record GettingStartedStep(bool Done, string Title, string HowTo, string Href, string LinkText);

public static class GettingStarted
{
    public static IReadOnlyList<GettingStartedStep> Evaluate(ConfigSnapshot config, StateSnapshot state)
    {
        var hasServers = config.Servers.Count > 0;
        var allConnected = hasServers && config.Servers.All(s => HasSuccess(s, state));
        var hasRealOutput = config.Outputs.Any(o =>
            !string.Equals(o.Type, "json_stdout", StringComparison.OrdinalIgnoreCase));

        return
        [
            new(hasServers, "Add a server",
                "Add the VBR or VBAWS server you want monitored.", "servers", "Open Servers"),
            new(allConnected, "Let it connect",
                "Every server needs one successful run. If one fails, check it with veeam-vhc-aws test-connection -c <yaml>.",
                "help#how-do-i-test-a-server", "How to test a server"),
            new(hasRealOutput, "Send alerts somewhere",
                "Add email, a webhook, a JSON file or Prometheus so alerts reach you.", "outputs", "Open Outputs"),
            new(state.LastRun is not null, "Wait for the first scheduled run",
                "The scheduled task runs every 5 minutes by default.", "help#set-it-up-from-scratch", "How the schedule works"),
        ];
    }

    private static bool HasSuccess(ServerView server, StateSnapshot state)
    {
        var key = server.Type == "vbr" ? $"{server.Name}:repo_health" : $"{server.Name}:worker_health";
        return state.LastSuccessByKey.TryGetValue(key, out var at) && at != default;
    }
}
