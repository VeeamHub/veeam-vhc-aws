using System.Text;
using System.Text.Json;
using Serilog;
using VeeamVhcAws.Core.Models;
using VeeamVhcAws.Core.Output;

namespace VeeamVhcAws.Outputs;

public class WebhookHandler : IOutputHandler
{
    private static readonly ILogger Logger = Log.ForContext<WebhookHandler>();

    private static readonly Dictionary<Severity, string> SeverityColors = new()
    {
        [Severity.Ok] = "#36a64f",
        [Severity.Warning] = "#ff9900",
        [Severity.Critical] = "#ff0000",
        [Severity.Error] = "#cc0000",
    };

    private static readonly Dictionary<Severity, string> PagerDutySeverities = new()
    {
        [Severity.Ok] = "info", [Severity.Warning] = "warning",
        [Severity.Critical] = "critical", [Severity.Error] = "error",
    };

    private static readonly Dictionary<Severity, string> NtfyPriorities = new()
    {
        [Severity.Ok] = "low", [Severity.Warning] = "default",
        [Severity.Critical] = "high", [Severity.Error] = "urgent",
    };

    private static readonly Dictionary<Severity, string> NtfyTags = new()
    {
        [Severity.Ok] = "white_check_mark", [Severity.Warning] = "warning",
        [Severity.Critical] = "rotating_light", [Severity.Error] = "x",
    };

    private const int NtfyMaxBody = 3800;

    private readonly string _url;
    private readonly string _template;
    private readonly Severity _minSeverity;
    private readonly bool _deduplicate;
    private readonly bool _allowLoopback;
    private readonly List<string> _logs = new();

    internal IReadOnlyList<string> Logs => _logs;

    public WebhookHandler(string url, string template = "generic",
        string minSeverity = "ok", bool deduplicate = true, bool allowLoopback = false)
    {
        _url = url;
        _template = template.ToLowerInvariant();
        _minSeverity = SeverityExtensions.ParseSeverity(minSeverity);
        _deduplicate = deduplicate;
        _allowLoopback = allowLoopback;
    }

    private bool ShouldSend(IReadOnlyList<MonitorResult> results)
    {
        var minOrder = _minSeverity.Rank();
        return results.Any(r => r.OverallSeverity.Rank() >= minOrder);
    }

    // --- Shared building blocks ---

    private static string Upper(Severity severity) => severity.ToLowerString().ToUpperInvariant();

    private static Severity Worst(IReadOnlyList<MonitorResult> results) =>
        results.Aggregate(Severity.Ok, (acc, r) => r.OverallSeverity.Rank() > acc.Rank() ? r.OverallSeverity : acc);

    private static bool IsCorrelation(MonitorResult result) =>
        result.Monitor.ToLowerString() == "cross_correlation";

    /// <summary>"monitor (server)", or just "monitor" when the result has no server.</summary>
    private static string MonitorLabel(MonitorResult result) =>
        !string.IsNullOrEmpty(result.Server)
            ? $"{result.Monitor.ToLowerString()} ({result.Server})"
            : result.Monitor.ToLowerString();

    /// <summary>Drops a leading "[server] " from a resource that already names its server.</summary>
    private static string StripServerPrefix(string resource, string server) =>
        !string.IsNullOrEmpty(server) && resource.StartsWith($"[{server}] ")
            ? resource[($"[{server}] ".Length)..]
            : resource;

    /// <summary>Non-OK, non-metric findings: what the summary templates list as issues.</summary>
    private static IEnumerable<Finding> IssueFindings(MonitorResult result) =>
        result.Findings.Where(f => f.Severity != Severity.Ok && f.MetricName == null);

    private static string FindingLines(IEnumerable<Finding> findings, string bullet) =>
        string.Join("\n", findings.Select(f => $"{bullet} [{Upper(f.Severity)}] {f.Resource}: {f.Message}"));

    private static Dictionary<string, object> SlackAttachment(string color, Dictionary<string, object> block) =>
        new()
        {
            ["color"] = color,
            ["blocks"] = new List<object> { block },
        };

    private static Dictionary<string, object> SlackBlock(string blockType, string textType, string text) =>
        new()
        {
            ["type"] = blockType,
            ["text"] = new Dictionary<string, object> { ["type"] = textType, ["text"] = text },
        };

    private static Dictionary<string, object> SlackSection(string color, string mrkdwn) =>
        SlackAttachment(color, SlackBlock("section", "mrkdwn", mrkdwn));

    private static Dictionary<string, object> TeamsCard(string themeColor, string summary,
        List<Dictionary<string, object>> sections) =>
        new()
        {
            ["@type"] = "MessageCard",
            ["@context"] = "http://schema.org/extensions",
            ["themeColor"] = themeColor.TrimStart('#'),
            ["summary"] = summary,
            ["sections"] = sections,
        };

    private static Dictionary<string, object> Fact(string name, string value) =>
        new() { ["name"] = name, ["value"] = value };

    private static Dictionary<string, object> NtfyPayload(string title, Severity worst, List<string> lines)
    {
        var body = string.Join("\n", lines).Trim();
        if (body.Length > NtfyMaxBody)
            body = body[..NtfyMaxBody] + "\n... (truncated)";

        return new Dictionary<string, object>
        {
            ["_ntfy_headers"] = new Dictionary<string, object>
            {
                ["Title"] = $"{title}: {Upper(worst)}",
                ["Priority"] = NtfyPriorities.GetValueOrDefault(worst, "default"),
                ["Tags"] = NtfyTags.GetValueOrDefault(worst, "bell"),
                ["Markdown"] = "yes",
            },
            ["_ntfy_body"] = body,
        };
    }

    // --- Alert templates ---

    private Dictionary<string, object> FormatSlack(IReadOnlyList<MonitorResult> results)
    {
        var attachments = results
            .Select(result => SlackSection(
                SeverityColors.GetValueOrDefault(result.OverallSeverity, "#808080"),
                $"*{result.Monitor.ToLowerString()}* — {Upper(result.OverallSeverity)}\nDuration: {result.DurationMs}ms\n{FindingLines(result.Findings, "•")}"))
            .ToList();
        return new Dictionary<string, object> { ["attachments"] = attachments };
    }

    private Dictionary<string, object> FormatTeams(IReadOnlyList<MonitorResult> results)
    {
        var sections = new List<Dictionary<string, object>>();
        var overallColor = "#808080";

        foreach (var result in results)
        {
            overallColor = SeverityColors.GetValueOrDefault(result.OverallSeverity, overallColor);
            sections.Add(new Dictionary<string, object>
            {
                ["activityTitle"] = $"Veeam VHC AWS: {result.Monitor.ToLowerString()}",
                ["facts"] = new List<Dictionary<string, object>>
                {
                    Fact("Monitor", result.Monitor.ToLowerString()),
                    Fact("Severity", Upper(result.OverallSeverity)),
                    Fact("Duration", $"{result.DurationMs}ms"),
                    Fact("Findings", result.Findings.Count.ToString()),
                },
                ["text"] = FindingLines(result.Findings, "-"),
            });
        }

        return TeamsCard(overallColor, "Veeam VHC AWS Results", sections);
    }

    private Dictionary<string, object> FormatPagerDuty(IReadOnlyList<MonitorResult> results)
    {
        var worst = Worst(results);
        var summaryParts = results.Select(r =>
            $"{r.Monitor.ToLowerString()}: {r.OverallSeverity.ToLowerString()} ({r.Findings.Count} findings)");

        return new Dictionary<string, object>
        {
            ["routing_key"] = "",
            ["event_action"] = worst != Severity.Ok ? "trigger" : "resolve",
            ["payload"] = new Dictionary<string, object>
            {
                ["summary"] = string.Join("; ", summaryParts),
                ["severity"] = PagerDutySeverities.GetValueOrDefault(worst, "info"),
                ["source"] = "veeam-vhc-aws",
                ["component"] = "veeam-backup",
                ["custom_details"] = new Dictionary<string, object>
                {
                    ["results"] = results.Select(r => r.ToDictionary()).ToList(),
                },
            },
        };
    }

    private Dictionary<string, object> FormatGeneric(IReadOnlyList<MonitorResult> results)
    {
        return new Dictionary<string, object>
        {
            ["results"] = results.Select(r => r.ToDictionary()).ToList()
        };
    }

    private Dictionary<string, object> FormatNtfy(IReadOnlyList<MonitorResult> results)
    {
        var lines = new List<string>();
        foreach (var result in results)
        {
            if (result.OverallSeverity == Severity.Ok) continue;
            if (IsCorrelation(result)) continue;

            var serverLabel = !string.IsNullOrEmpty(result.Server) ? $" ({result.Server})" : "";
            var alertable = result.Findings
                .Where(f => f.MetricName == null
                    && f.Severity != Severity.Ok
                    && !(_deduplicate && f.Details.ContainsKey("_seen_before") && f.Details["_seen_before"] is true))
                .ToList();

            if (alertable.Count == 0) continue;

            lines.Add($"**{result.Monitor.ToLowerString()}{serverLabel}**");
            var hasConnectionFindings = alertable.Any(f => f.Resource.Contains("connection"));
            foreach (var f in alertable.Take(15))
                lines.Add($"- {StripServerPrefix(f.Resource, result.Server)}: {TruncateMessage(f.Message)}");
            if (alertable.Count > 15)
                lines.Add($"- ... and {alertable.Count - 15} more");

            if (result.Errors.Count > 0 && !hasConnectionFindings)
                foreach (var e in result.Errors.Take(3))
                    lines.Add($"- ERROR: {(e.Length > 120 ? e[..120] : e)}");

            lines.Add("");
        }

        // OK monitor summary
        var okMonitors = results
            .Where(r => r.OverallSeverity == Severity.Ok && !IsCorrelation(r))
            .ToList();
        if (okMonitors.Count > 0)
            lines.Add($"OK: {string.Join(", ", okMonitors.Select(MonitorLabel))}");

        // Resolved findings
        var resolved = results
            .SelectMany(result => result.Findings
                .Where(f => f.Message.StartsWith("RESOLVED:"))
                .Select(f => $"- {StripServerPrefix(f.Resource, result.Server)}: {f.Message}"))
            .ToList();
        if (resolved.Count > 0)
        {
            lines.Add("**Resolved:**");
            lines.AddRange(resolved.Take(10));
            lines.Add("");
        }

        if (lines.Count == 0)
            lines.Add("All monitors OK");

        return NtfyPayload("veeam-vhc-aws", Worst(results), lines);
    }

    private static string BoldWorkloads(string message)
    {
        var idx = message.IndexOf("Workloads:", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return message;
        var prefix = message[..(idx + "Workloads:".Length)];
        var list = message[(idx + "Workloads:".Length)..].Trim();
        return $"{prefix} **{list}**";
    }

    private static string TruncateMessage(string message, int maxLength = 160)
    {
        if (message.Length <= maxLength) return message;

        // Smart truncation: summarize "Workloads: a, b, c, ..." as "Workloads: N items"
        var workloadsIdx = message.IndexOf("Workloads:", StringComparison.OrdinalIgnoreCase);
        if (workloadsIdx >= 0)
        {
            var prefix = message[..workloadsIdx];
            var workloadList = message[(workloadsIdx + "Workloads:".Length)..].Trim();
            var count = workloadList.Split(',').Length;
            // Extract the common path prefix for context (e.g. "\syn01\docker")
            var firstItem = workloadList.Split(',')[0].Trim();
            var contextHint = firstItem.Contains(' ')
                ? firstItem[..firstItem.LastIndexOf(' ')].TrimEnd('\\')
                : firstItem;
            return $"{prefix}Workloads: {count} items ({contextHint}…)";
        }

        return message[..maxLength] + "…";
    }

    // --- Daily-summary templates ---

    private Dictionary<string, object> FormatNtfySummary(IReadOnlyList<MonitorResult> results)
    {
        var now = DateTime.Now;
        var dateStr = $"{now:MMMM} {now.Day}, {now.Year}";

        var issueLines = new List<string>();
        var healthyMonitors = new List<string>();

        foreach (var result in results)
        {
            if (IsCorrelation(result)) continue;

            if (result.OverallSeverity == Severity.Ok)
            {
                healthyMonitors.Add(MonitorLabel(result));
                continue;
            }

            foreach (var f in IssueFindings(result))
            {
                var icon = f.Severity is Severity.Critical or Severity.Error ? "🔴" : "⚠️";
                var serverTag = !string.IsNullOrEmpty(result.Server) ? $"[{result.Server}] " : "";
                issueLines.Add($"{icon} {serverTag}{StripServerPrefix(f.Resource, result.Server)} — {BoldWorkloads(TruncateMessage(f.Message))}");
            }
        }

        var lines = new List<string> { $"**Daily Veeam Health Summary — {dateStr}**", "" };

        if (issueLines.Count > 0)
        {
            lines.Add($"**Issues ({issueLines.Count}):**");
            lines.AddRange(issueLines);
        }
        else
        {
            lines.Add("All monitors healthy");
        }

        if (healthyMonitors.Count > 0)
        {
            lines.Add("");
            lines.Add($"**Healthy:** {string.Join(", ", healthyMonitors)}");
        }

        return NtfyPayload("Daily Summary", Worst(results), lines);
    }

    private Dictionary<string, object> FormatSlackSummary(IReadOnlyList<MonitorResult> results)
    {
        var attachments = new List<Dictionary<string, object>>
        {
            SlackAttachment("#0076D7", SlackBlock("header", "plain_text", "Daily Veeam VHC AWS Summary")),
        };

        var healthyServers = new List<string>();
        foreach (var result in results)
        {
            if (IsCorrelation(result)) continue;

            if (result.OverallSeverity == Severity.Ok)
            {
                healthyServers.Add(MonitorLabel(result));
                continue;
            }

            var issues = IssueFindings(result).ToList();
            if (issues.Count > 0)
            {
                var color = SeverityColors.GetValueOrDefault(result.OverallSeverity, "#808080");
                var serverLabel = !string.IsNullOrEmpty(result.Server) ? $" ({result.Server})" : "";
                attachments.Add(SlackSection(color,
                    $"*{result.Monitor.ToLowerString()}{serverLabel}*\n{FindingLines(issues, "•")}"));
            }
        }

        if (healthyServers.Count > 0)
            attachments.Add(SlackSection("#36a64f", $"*Healthy:* {string.Join(", ", healthyServers)}"));

        return new Dictionary<string, object> { ["attachments"] = attachments };
    }

    private Dictionary<string, object> FormatTeamsSummary(IReadOnlyList<MonitorResult> results)
    {
        var sections = new List<Dictionary<string, object>>();
        var overallColor = "#808080";
        var healthyMonitors = new List<string>();

        foreach (var result in results)
        {
            if (IsCorrelation(result)) continue;

            if (result.OverallSeverity == Severity.Ok)
            {
                healthyMonitors.Add(MonitorLabel(result));
                continue;
            }

            overallColor = SeverityColors.GetValueOrDefault(result.OverallSeverity, overallColor);
            sections.Add(new Dictionary<string, object>
            {
                ["activityTitle"] = result.Monitor.ToLowerString(),
                ["facts"] = new List<Dictionary<string, object>>
                {
                    Fact("Monitor", result.Monitor.ToLowerString()),
                    Fact("Server", !string.IsNullOrEmpty(result.Server) ? result.Server : "N/A"),
                    Fact("Severity", Upper(result.OverallSeverity)),
                    Fact("Findings", result.Findings.Count.ToString()),
                },
                ["text"] = FindingLines(IssueFindings(result), "-"),
            });
        }

        if (healthyMonitors.Count > 0)
        {
            sections.Add(new Dictionary<string, object>
            {
                ["activityTitle"] = "Healthy Monitors",
                ["facts"] = new List<Dictionary<string, object>>
                {
                    Fact("Healthy monitors", string.Join(", ", healthyMonitors)),
                },
                ["text"] = "",
            });
        }

        return TeamsCard(overallColor, "Daily Veeam VHC AWS Summary", sections);
    }

    public void Emit(IReadOnlyList<MonitorResult> results)
    {
        var isSummary = results.IsSummary();

        if (!isSummary && !ShouldSend(results))
        {
            Record($"Webhook skipped for {HostForLog(_url)} — severity below threshold ({_minSeverity})");
            return;
        }

        Dictionary<string, object> payload;
        if (isSummary)
        {
            payload = _template switch
            {
                "ntfy" => FormatNtfySummary(results),
                "slack" => FormatSlackSummary(results),
                "teams" => FormatTeamsSummary(results),
                "pagerduty" => FormatPagerDuty(results),
                _ => FormatGeneric(results),
            };
        }
        else
        {
            payload = _template switch
            {
                "slack" => FormatSlack(results),
                "teams" => FormatTeams(results),
                "pagerduty" => FormatPagerDuty(results),
                "ntfy" => FormatNtfy(results),
                _ => FormatGeneric(results),
            };
        }

        // Dedup applies to every template: skip when nothing is new and nothing resolved.
        if (!isSummary && _deduplicate && !results.HasNewOrResolvedFindings())
        {
            Record($"Webhook skipped for {HostForLog(_url)} — no new or resolved findings (dedup)");
            return;
        }

        var decision = OutboundUrlDecision.Decide(_url, _allowLoopback);
        if (!decision.Allowed)
        {
            Record($"Webhook rejected for {decision.Host}: {decision.Reason}");
            return;
        }

        try
        {
            using var client = OutboundUrlDecision.CreateClient();
            HttpResponseMessage response;

            if (_template == "ntfy")
            {
                var ntfyHeaders = payload.GetValueOrDefault("_ntfy_headers") as Dictionary<string, object>
                    ?? new Dictionary<string, object>();
                var body = payload.GetValueOrDefault("_ntfy_body")?.ToString() ?? "";

                var request = new HttpRequestMessage(HttpMethod.Post, _url)
                {
                    Content = new StringContent(body, Encoding.UTF8, "text/plain")
                };
                foreach (var (key, value) in ntfyHeaders)
                    request.Headers.TryAddWithoutValidation(key, value.ToString());

                response = client.SendAsync(request).GetAwaiter().GetResult();
            }
            else
            {
                var json = JsonSerializer.Serialize(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                response = client.PostAsync(_url, content).GetAwaiter().GetResult();
            }

            response.EnsureSuccessStatusCode();
            Record($"Webhook sent to {HostForLog(_url)} (template={_template}, status={(int)response.StatusCode})");
        }
        catch (Exception e)
        {
            Record($"Webhook failed for {HostForLog(_url)}: {e.Message}");
        }
    }

    private void Record(string message)
    {
        _logs.Add(message);
        Logger.Information("{WebhookLog}", message);
    }

    private static string HostForLog(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host))
            return uri.Host;
        return "invalid-webhook-host";
    }
}
