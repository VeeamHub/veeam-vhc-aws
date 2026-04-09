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

    private readonly string _url;
    private readonly string _template;
    private readonly Severity _minSeverity;
    private readonly bool _deduplicate;

    public WebhookHandler(string url, string template = "generic",
        string minSeverity = "ok", bool deduplicate = true)
    {
        _url = url;
        _template = template.ToLowerInvariant();
        _minSeverity = SeverityExtensions.ParseSeverity(minSeverity);
        _deduplicate = deduplicate;
    }

    private bool ShouldSend(IReadOnlyList<MonitorResult> results)
    {
        var minOrder = _minSeverity.Rank();
        return results.Any(r => r.OverallSeverity.Rank() >= minOrder);
    }

    private Dictionary<string, object> FormatSlack(IReadOnlyList<MonitorResult> results)
    {
        var attachments = new List<Dictionary<string, object>>();
        foreach (var result in results)
        {
            var color = SeverityColors.GetValueOrDefault(result.OverallSeverity, "#808080");
            var findingLines = result.Findings
                .Select(f => $"• [{f.Severity.ToLowerString().ToUpperInvariant()}] {f.Resource}: {f.Message}")
                .ToList();

            attachments.Add(new Dictionary<string, object>
            {
                ["color"] = color,
                ["blocks"] = new List<object>
                {
                    new Dictionary<string, object>
                    {
                        ["type"] = "section",
                        ["text"] = new Dictionary<string, object>
                        {
                            ["type"] = "mrkdwn",
                            ["text"] = $"*{result.Monitor.ToLowerString()}* — {result.OverallSeverity.ToLowerString().ToUpperInvariant()}\nDuration: {result.DurationMs}ms\n{string.Join("\n", findingLines)}"
                        }
                    }
                }
            });
        }
        return new Dictionary<string, object> { ["attachments"] = attachments };
    }

    private Dictionary<string, object> FormatTeams(IReadOnlyList<MonitorResult> results)
    {
        var sections = new List<Dictionary<string, object>>();
        var overallColor = "#808080";

        foreach (var result in results)
        {
            overallColor = SeverityColors.GetValueOrDefault(result.OverallSeverity, overallColor);
            var facts = new List<Dictionary<string, object>>
            {
                new() { ["name"] = "Monitor", ["value"] = result.Monitor.ToLowerString() },
                new() { ["name"] = "Severity", ["value"] = result.OverallSeverity.ToLowerString().ToUpperInvariant() },
                new() { ["name"] = "Duration", ["value"] = $"{result.DurationMs}ms" },
                new() { ["name"] = "Findings", ["value"] = result.Findings.Count.ToString() },
            };
            sections.Add(new Dictionary<string, object>
            {
                ["activityTitle"] = $"Veeam VHC AWS: {result.Monitor.ToLowerString()}",
                ["facts"] = facts,
                ["text"] = string.Join("\n", result.Findings
                    .Select(f => $"- [{f.Severity.ToLowerString().ToUpperInvariant()}] {f.Resource}: {f.Message}")),
            });
        }

        return new Dictionary<string, object>
        {
            ["@type"] = "MessageCard",
            ["@context"] = "http://schema.org/extensions",
            ["themeColor"] = overallColor.TrimStart('#'),
            ["summary"] = "Veeam VHC AWS Results",
            ["sections"] = sections,
        };
    }

    private Dictionary<string, object> FormatPagerDuty(IReadOnlyList<MonitorResult> results)
    {
        var worst = results.Aggregate(Severity.Ok, (acc, r) =>
            r.OverallSeverity.Rank() > acc.Rank() ? r.OverallSeverity : acc);

        var pdSeverityMap = new Dictionary<Severity, string>
        {
            [Severity.Ok] = "info", [Severity.Warning] = "warning",
            [Severity.Critical] = "critical", [Severity.Error] = "error",
        };

        var summaryParts = results.Select(r =>
            $"{r.Monitor.ToLowerString()}: {r.OverallSeverity.ToLowerString()} ({r.Findings.Count} findings)");

        return new Dictionary<string, object>
        {
            ["routing_key"] = "",
            ["event_action"] = worst != Severity.Ok ? "trigger" : "resolve",
            ["payload"] = new Dictionary<string, object>
            {
                ["summary"] = string.Join("; ", summaryParts),
                ["severity"] = pdSeverityMap.GetValueOrDefault(worst, "info"),
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
        var worst = results.Aggregate(Severity.Ok, (acc, r) =>
            r.OverallSeverity.Rank() > acc.Rank() ? r.OverallSeverity : acc);

        var ntfyPriority = new Dictionary<Severity, string>
        {
            [Severity.Ok] = "low", [Severity.Warning] = "default",
            [Severity.Critical] = "high", [Severity.Error] = "urgent",
        };
        var ntfyTags = new Dictionary<Severity, string>
        {
            [Severity.Ok] = "white_check_mark", [Severity.Warning] = "warning",
            [Severity.Critical] = "rotating_light", [Severity.Error] = "x",
        };

        var lines = new List<string>();
        foreach (var result in results)
        {
            if (result.OverallSeverity == Severity.Ok) continue;
            if (result.Monitor.ToLowerString() == "cross_correlation") continue;

            var serverLabel = !string.IsNullOrEmpty(result.Server) ? $" ({result.Server})" : "";
            var nonMetricFindings = result.Findings.Where(f => f.MetricName == null).ToList();

            var alertable = nonMetricFindings
                .Where(f => f.Severity != Severity.Ok
                    && !(_deduplicate && f.Details.ContainsKey("_seen_before") && f.Details["_seen_before"] is true))
                .ToList();

            if (alertable.Count == 0) continue;

            lines.Add($"**{result.Monitor.ToLowerString()}{serverLabel}**");
            var hasConnectionFindings = alertable.Any(f => f.Resource.Contains("connection"));
            foreach (var f in alertable.Take(15))
            {
                var resource = f.Resource;
                if (!string.IsNullOrEmpty(result.Server) && resource.StartsWith($"[{result.Server}] "))
                    resource = resource[($"[{result.Server}] ".Length)..];
                lines.Add($"- {resource}: {TruncateMessage(f.Message)}");
            }
            if (alertable.Count > 15)
                lines.Add($"- ... and {alertable.Count - 15} more");

            if (result.Errors.Count > 0 && !hasConnectionFindings)
                foreach (var e in result.Errors.Take(3))
                    lines.Add($"- ERROR: {(e.Length > 120 ? e[..120] : e)}");

            lines.Add("");
        }

        // OK monitor summary
        var okMonitors = results
            .Where(r => r.OverallSeverity == Severity.Ok && r.Monitor.ToLowerString() != "cross_correlation")
            .ToList();
        if (okMonitors.Count > 0)
        {
            var okSummary = string.Join(", ", okMonitors.Select(r =>
                !string.IsNullOrEmpty(r.Server) ? $"{r.Monitor.ToLowerString()} ({r.Server})" : r.Monitor.ToLowerString()));
            lines.Add($"OK: {okSummary}");
        }

        // Resolved findings
        var resolved = new List<string>();
        foreach (var result in results)
            foreach (var f in result.Findings.Where(f => f.Message.StartsWith("RESOLVED:")))
            {
                var resource = f.Resource;
                if (!string.IsNullOrEmpty(result.Server) && resource.StartsWith($"[{result.Server}] "))
                    resource = resource[($"[{result.Server}] ".Length)..];
                resolved.Add($"- {resource}: {f.Message}");
            }
        if (resolved.Count > 0)
        {
            lines.Add("**Resolved:**");
            foreach (var r in resolved.Take(10))
                lines.Add(r);
            lines.Add("");
        }

        if (lines.Count == 0)
            lines.Add("All monitors OK");

        var body = string.Join("\n", lines).Trim();
        if (body.Length > 3800)
            body = body[..3800] + "\n... (truncated)";

        return new Dictionary<string, object>
        {
            ["_ntfy_headers"] = new Dictionary<string, object>
            {
                ["Title"] = $"veeam-vhc-aws: {worst.ToLowerString().ToUpperInvariant()}",
                ["Priority"] = ntfyPriority.GetValueOrDefault(worst, "default"),
                ["Tags"] = ntfyTags.GetValueOrDefault(worst, "bell"),
                ["Markdown"] = "yes",
            },
            ["_ntfy_body"] = body,
        };
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

    private Dictionary<string, object> FormatNtfySummary(IReadOnlyList<MonitorResult> results)
    {
        var worst = results.Aggregate(Severity.Ok, (acc, r) =>
            r.OverallSeverity.Rank() > acc.Rank() ? r.OverallSeverity : acc);

        var ntfyPriority = new Dictionary<Severity, string>
        {
            [Severity.Ok] = "low", [Severity.Warning] = "default",
            [Severity.Critical] = "high", [Severity.Error] = "urgent",
        };
        var ntfyTags = new Dictionary<Severity, string>
        {
            [Severity.Ok] = "white_check_mark", [Severity.Warning] = "warning",
            [Severity.Critical] = "rotating_light", [Severity.Error] = "x",
        };

        var now = DateTime.Now;
        var dateStr = $"{now:MMMM} {now.Day}, {now.Year}";

        var issueLines = new List<string>();
        var healthyMonitors = new List<string>();

        foreach (var result in results)
        {
            if (result.Monitor.ToLowerString() == "cross_correlation") continue;

            if (result.OverallSeverity == Severity.Ok)
            {
                var label = !string.IsNullOrEmpty(result.Server)
                    ? $"{result.Monitor.ToLowerString()} ({result.Server})" : result.Monitor.ToLowerString();
                healthyMonitors.Add(label);
                continue;
            }

            foreach (var f in result.Findings.Where(f => f.MetricName == null && f.Severity != Severity.Ok))
            {
                var resource = f.Resource;
                if (!string.IsNullOrEmpty(result.Server) && resource.StartsWith($"[{result.Server}] "))
                    resource = resource[($"[{result.Server}] ".Length)..];
                var icon = f.Severity is Severity.Critical or Severity.Error ? "🔴" : "⚠️";
                var serverTag = !string.IsNullOrEmpty(result.Server) ? $"[{result.Server}] " : "";
                issueLines.Add($"{icon} {serverTag}{resource} — {BoldWorkloads(TruncateMessage(f.Message))}");
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

        var body = string.Join("\n", lines).Trim();
        if (body.Length > 3800)
            body = body[..3800] + "\n... (truncated)";

        return new Dictionary<string, object>
        {
            ["_ntfy_headers"] = new Dictionary<string, object>
            {
                ["Title"] = $"Daily Summary: {worst.ToLowerString().ToUpperInvariant()}",
                ["Priority"] = ntfyPriority.GetValueOrDefault(worst, "default"),
                ["Tags"] = ntfyTags.GetValueOrDefault(worst, "bell"),
                ["Markdown"] = "yes",
            },
            ["_ntfy_body"] = body,
        };
    }

    private Dictionary<string, object> FormatSlackSummary(IReadOnlyList<MonitorResult> results)
    {
        var attachments = new List<Dictionary<string, object>>
        {
            new()
            {
                ["color"] = "#0076D7",
                ["blocks"] = new List<object>
                {
                    new Dictionary<string, object>
                    {
                        ["type"] = "header",
                        ["text"] = new Dictionary<string, object>
                        {
                            ["type"] = "plain_text",
                            ["text"] = "Daily Veeam VHC AWS Summary",
                        }
                    }
                }
            }
        };

        var healthyServers = new List<string>();
        foreach (var result in results)
        {
            if (result.Monitor.ToLowerString() == "cross_correlation") continue;

            if (result.OverallSeverity == Severity.Ok)
            {
                var label = !string.IsNullOrEmpty(result.Server)
                    ? $"{result.Monitor.ToLowerString()} ({result.Server})" : result.Monitor.ToLowerString();
                healthyServers.Add(label);
                continue;
            }

            var color = SeverityColors.GetValueOrDefault(result.OverallSeverity, "#808080");
            var findingLines = result.Findings
                .Where(f => f.Severity != Severity.Ok && f.MetricName == null)
                .Select(f => $"• [{f.Severity.ToLowerString().ToUpperInvariant()}] {f.Resource}: {f.Message}")
                .ToList();

            if (findingLines.Count > 0)
            {
                var serverLabel = !string.IsNullOrEmpty(result.Server) ? $" ({result.Server})" : "";
                attachments.Add(new Dictionary<string, object>
                {
                    ["color"] = color,
                    ["blocks"] = new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["type"] = "section",
                            ["text"] = new Dictionary<string, object>
                            {
                                ["type"] = "mrkdwn",
                                ["text"] = $"*{result.Monitor.ToLowerString()}{serverLabel}*\n{string.Join("\n", findingLines)}"
                            }
                        }
                    }
                });
            }
        }

        if (healthyServers.Count > 0)
        {
            attachments.Add(new Dictionary<string, object>
            {
                ["color"] = "#36a64f",
                ["blocks"] = new List<object>
                {
                    new Dictionary<string, object>
                    {
                        ["type"] = "section",
                        ["text"] = new Dictionary<string, object>
                        {
                            ["type"] = "mrkdwn",
                            ["text"] = $"*Healthy:* {string.Join(", ", healthyServers)}",
                        }
                    }
                }
            });
        }

        return new Dictionary<string, object> { ["attachments"] = attachments };
    }

    private Dictionary<string, object> FormatTeamsSummary(IReadOnlyList<MonitorResult> results)
    {
        var sections = new List<Dictionary<string, object>>();
        var overallColor = "#808080";
        var healthyMonitors = new List<string>();

        foreach (var result in results)
        {
            if (result.Monitor.ToLowerString() == "cross_correlation") continue;

            if (result.OverallSeverity == Severity.Ok)
            {
                var label = !string.IsNullOrEmpty(result.Server)
                    ? $"{result.Monitor.ToLowerString()} ({result.Server})" : result.Monitor.ToLowerString();
                healthyMonitors.Add(label);
                continue;
            }

            overallColor = SeverityColors.GetValueOrDefault(result.OverallSeverity, overallColor);
            var facts = new List<Dictionary<string, object>>
            {
                new() { ["name"] = "Monitor", ["value"] = result.Monitor.ToLowerString() },
                new() { ["name"] = "Server", ["value"] = !string.IsNullOrEmpty(result.Server) ? result.Server : "N/A" },
                new() { ["name"] = "Severity", ["value"] = result.OverallSeverity.ToLowerString().ToUpperInvariant() },
                new() { ["name"] = "Findings", ["value"] = result.Findings.Count.ToString() },
            };
            sections.Add(new Dictionary<string, object>
            {
                ["activityTitle"] = result.Monitor.ToLowerString(),
                ["facts"] = facts,
                ["text"] = string.Join("\n", result.Findings
                    .Where(f => f.Severity != Severity.Ok && f.MetricName == null)
                    .Select(f => $"- [{f.Severity.ToLowerString().ToUpperInvariant()}] {f.Resource}: {f.Message}")),
            });
        }

        if (healthyMonitors.Count > 0)
        {
            sections.Add(new Dictionary<string, object>
            {
                ["activityTitle"] = "Healthy Monitors",
                ["facts"] = new List<Dictionary<string, object>>
                {
                    new() { ["name"] = "Healthy monitors", ["value"] = string.Join(", ", healthyMonitors) }
                },
                ["text"] = "",
            });
        }

        return new Dictionary<string, object>
        {
            ["@type"] = "MessageCard",
            ["@context"] = "http://schema.org/extensions",
            ["themeColor"] = overallColor.TrimStart('#'),
            ["summary"] = "Daily Veeam VHC AWS Summary",
            ["sections"] = sections,
        };
    }

    public void Emit(IReadOnlyList<MonitorResult> results)
    {
        var isSummary = results.Any(r => r.Metadata.ContainsKey("summary") && r.Metadata["summary"] is true);

        if (!isSummary && !ShouldSend(results))
        {
            Logger.Information("Webhook skipped for {Url} — severity below threshold ({MinSeverity})", _url, _minSeverity);
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

        // For ntfy with dedup: skip if no new alertable findings
        if (!isSummary && _template == "ntfy" && _deduplicate)
        {
            var hasNewAlerts = results.SelectMany(r => r.Findings).Any(f =>
                f.Severity != Severity.Ok
                && f.MetricName == null
                && !(f.Details.ContainsKey("_seen_before") && f.Details["_seen_before"] is true)
                && !f.Message.StartsWith("RESOLVED:"));
            var hasResolved = results.SelectMany(r => r.Findings).Any(f => f.Message.StartsWith("RESOLVED:"));

            if (!hasNewAlerts && !hasResolved)
            {
                Logger.Information("Webhook skipped for {Url} — no new or resolved findings (dedup)", _url);
                return;
            }
        }

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
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
            Logger.Information("Webhook sent to {Url} (template={Template}, status={StatusCode})",
                _url, _template, (int)response.StatusCode);
        }
        catch (Exception e)
        {
            Logger.Error("Webhook failed for {Url}: {Error}", _url, e.Message);
        }
    }
}
