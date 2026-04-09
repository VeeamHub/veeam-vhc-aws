using System.Diagnostics;
using Serilog;
using VeeamVhcAws.Core.Config;
using VeeamVhcAws.Core.Models;
using VeeamVhcAws.Core.Patterns;
using VeeamVhcAws.Infrastructure;

namespace VeeamVhcAws.Monitors;

public class RepoHealthMonitor : IMonitor
{
    private static readonly ILogger Logger = Log.ForContext<RepoHealthMonitor>();
    private readonly Dictionary<string, object> _config;

    public MonitorType Type => MonitorType.RepoHealth;
    public IReadOnlyList<string> RequiredConnections => new[] { "vbr" };

    public RepoHealthMonitor(Dictionary<string, object> config)
    {
        _config = config;
    }

    private Dictionary<string, object> GetConfig() => _config.GetSection("repo_health");

    public MonitorResult Run(ServerContext serverContext, PatternEngine? patternEngine)
    {
        var sw = Stopwatch.StartNew();
        var findings = new List<Finding>();
        var errors = new List<string>();
        var cfg = GetConfig();
        var thresholds = cfg.GetSection("thresholds");
        var warningPct = thresholds.Get("free_space_warning_pct", 15);
        var criticalPct = thresholds.Get("free_space_critical_pct", 5);
        var client = serverContext.VbrClient!;

        // --- 1. Repository capacity check ---
        List<Dictionary<string, object>> repoStates;
        try
        {
            repoStates = client.GetRepositoryStates();
        }
        catch (Exception e)
        {
            errors.Add($"Failed to fetch repository states: {e.Message}");
            repoStates = new List<Dictionary<string, object>>();
        }

        bool hasExternalRepos = false;

        foreach (var repo in repoStates)
        {
            var repoName = repo.GetApiString("name", "Name", "unknown");
            var capacityGb = repo.GetApiDouble("capacityGB", "CapacityGB", 0);
            var freeGb = repo.GetApiDouble("freeGB", "FreeGB", 0);
            var repoType = repo.GetApiString("type", "Type", "");

            if (!string.IsNullOrEmpty(repoType) && repoType.Contains("external", StringComparison.OrdinalIgnoreCase))
                hasExternalRepos = true;

            // Unreachable repo
            if (capacityGb == 0)
            {
                findings.Add(new Finding(Severity.Error, $"repo:{repoName}", "repo unreachable",
                    new Dictionary<string, object> { ["capacityGB"] = capacityGb, ["freeGB"] = freeGb }));
                findings.Add(new Finding(Severity.Ok, $"repo:{repoName}", "repo health metric",
                    metricName: "veeam_repo_healthy", metricValue: 0.0));
                continue;
            }

            var freePct = freeGb / capacityGb * 100;

            if (freePct < criticalPct)
            {
                findings.Add(new Finding(Severity.Critical, $"repo:{repoName}",
                    $"Repository critically low on space: {freePct:F1}% free",
                    new Dictionary<string, object> { ["capacityGB"] = capacityGb, ["freeGB"] = freeGb, ["freePct"] = freePct }));
            }
            else if (freePct < warningPct)
            {
                findings.Add(new Finding(Severity.Warning, $"repo:{repoName}",
                    $"Repository low on space: {freePct:F1}% free",
                    new Dictionary<string, object> { ["capacityGB"] = capacityGb, ["freeGB"] = freeGb, ["freePct"] = freePct }));
            }
            else
            {
                findings.Add(new Finding(Severity.Ok, $"repo:{repoName}",
                    $"Repository healthy: {freePct:F1}% free",
                    new Dictionary<string, object> { ["capacityGB"] = capacityGb, ["freeGB"] = freeGb, ["freePct"] = freePct }));
            }

            // Metric findings
            findings.Add(new Finding(Severity.Ok, $"repo:{repoName}", "capacity metric",
                metricName: "veeam_repo_capacity_gb", metricValue: capacityGb));
            findings.Add(new Finding(Severity.Ok, $"repo:{repoName}", "free space metric",
                metricName: "veeam_repo_free_gb", metricValue: freeGb));
            findings.Add(new Finding(Severity.Ok, $"repo:{repoName}", "free pct metric",
                metricName: "veeam_repo_free_pct", metricValue: freePct));
            findings.Add(new Finding(Severity.Ok, $"repo:{repoName}", "repo health metric",
                metricName: "veeam_repo_healthy", metricValue: freePct >= criticalPct ? 1.0 : 0.0));
        }

        // --- 2. Session-based repo issue detection ---
        var lookbackHours = cfg.Get("session_lookback_hours",
            cfg.Get("external_maintenance_lookback_hours", 24));
        int credentialExpiredCount = 0;
        int sessionWarningCount = 0;

        var repoSessionTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "externalmaintenance", "externalmaintenancesession",
            "configurationresynchronize",
            "repositoryrescan", "repositorymaintenance",
        };

        List<Dictionary<string, object>> sessions;
        try
        {
            sessions = client.GetSessions(lookbackHours: lookbackHours);
        }
        catch (Exception e)
        {
            errors.Add($"Failed to fetch sessions: {e.Message}");
            sessions = new List<Dictionary<string, object>>();
        }

        // Group problem sessions by (type, status, pattern_category)
        var sessionIssues = new Dictionary<(string, string, string), (int Count, Severity Severity, string ErrorText, string SessionName)>();

        foreach (var session in sessions)
        {
            var sessionType = session.GetApiString("sessionType", "type", "");
            if (string.IsNullOrEmpty(sessionType))
                sessionType = session.GetApiString("type", "Type", "");
            sessionType = sessionType.ToLowerInvariant();

            if (!repoSessionTypes.Contains(sessionType))
                continue;

            var resultObj = session.GetApi("result", "Result", null);
            string resultStatus;
            string errorText;

            if (resultObj is Dictionary<string, object> resultDict)
            {
                resultStatus = resultDict.GetApiString("result", "Result", "").ToLowerInvariant();
                errorText = resultDict.GetApiString("message", "Message", "");
            }
            else
            {
                resultStatus = resultObj?.ToString()?.ToLowerInvariant() ?? "";
                errorText = resultObj?.ToString() ?? "";
            }

            if (resultStatus != "warning" && resultStatus != "failed")
                continue;

            sessionWarningCount++;
            var sessionName = session.GetApiString("name", "Name", "unknown");
            var severity = resultStatus == "failed" ? Severity.Critical : Severity.Warning;

            string patternCat = "";
            if (patternEngine != null && !string.IsNullOrEmpty(errorText))
            {
                var matched = patternEngine.Classify(errorText);
                if (matched != null)
                {
                    patternCat = matched.Category;
                    if (patternCat == "credential")
                    {
                        credentialExpiredCount++;
                        severity = Severity.Critical;
                    }
                    else if (patternCat is "auth" or "s3")
                    {
                        severity = Severity.Critical;
                    }
                }
            }

            var key = (sessionType, resultStatus, patternCat);
            if (!sessionIssues.TryGetValue(key, out var existing))
            {
                sessionIssues[key] = (1, severity, errorText, sessionName);
            }
            else
            {
                var worstSev = severity.Rank() > existing.Severity.Rank() ? severity : existing.Severity;
                sessionIssues[key] = (existing.Count + 1, worstSev, existing.ErrorText, existing.SessionName);
            }
        }

        // Emit one finding per unique issue type
        foreach (var ((sessionType, resultStatus, patternCat), info) in sessionIssues)
        {
            var countSuffix = info.Count > 1 ? $" ({info.Count}x in last {lookbackHours}h)" : "";
            var errorDetail = !string.IsNullOrEmpty(info.ErrorText) ? info.ErrorText : "check VBR console for details";

            string msg;
            if (patternCat == "credential")
                msg = $"Credential failure in {sessionType}{countSuffix}: {errorDetail}";
            else if (patternCat is "auth" or "s3")
                msg = $"S3/auth failure in {sessionType}{countSuffix}: {errorDetail}";
            else
                msg = $"{sessionType} {resultStatus}{countSuffix}: {errorDetail}";

            findings.Add(new Finding(info.Severity, $"session:{info.SessionName}", msg,
                new Dictionary<string, object>
                {
                    ["session_type"] = sessionType,
                    ["session_name"] = info.SessionName,
                    ["result"] = resultStatus,
                    ["count"] = info.Count,
                    ["lookback_hours"] = lookbackHours,
                    ["error"] = info.ErrorText,
                }));

            Logger.Information("Repo session issue: {Type} {Status} ({Count}x): {Error}",
                sessionType, resultStatus, info.Count, info.ErrorText);
        }

        if (sessions.Count == 0 && hasExternalRepos)
        {
            findings.Add(new Finding(Severity.Warning, "sessions",
                "No sessions found in lookback window",
                new Dictionary<string, object> { ["lookback_hours"] = lookbackHours }));
        }

        // Metrics
        findings.Add(new Finding(Severity.Ok, "session-health", "repo session warnings metric",
            metricName: "veeam_repo_session_warnings", metricValue: sessionWarningCount));
        findings.Add(new Finding(Severity.Ok, "session-health", "credential expired metric",
            metricName: "veeam_repo_credential_expired", metricValue: credentialExpiredCount));

        // --- 3. Scale-out backup repository check ---
        List<Dictionary<string, object>> sobrs;
        try
        {
            sobrs = client.GetScaleoutRepositories();
        }
        catch (Exception e)
        {
            errors.Add($"Failed to fetch scale-out repositories: {e.Message}");
            sobrs = new List<Dictionary<string, object>>();
        }

        foreach (var sobr in sobrs)
        {
            var sobrName = sobr.GetApiString("name", "Name", "unknown");
            var perfTier = sobr.GetSection("performanceTier");
            var capTier = sobr.GetSection("capacityTier");
            var archiveTier = sobr.GetSection("archiveTier");

            // Check performance tier extents
            List<Dictionary<string, object>> perfExtents;
            if (perfTier.TryGetValue("performanceExtents", out var pe) || perfTier.TryGetValue("extents", out pe))
            {
                perfExtents = pe switch
                {
                    List<object> list => list.OfType<Dictionary<string, object>>().ToList(),
                    List<Dictionary<string, object>> typed => typed,
                    _ => new List<Dictionary<string, object>>()
                };
            }
            else
            {
                perfExtents = new List<Dictionary<string, object>>();
            }

            foreach (var extent in perfExtents)
            {
                var extentName = extent.GetApiString("name", "Name", "unknown");
                var extentStatus = extent.GetApiString("status", "Status", "").ToLowerInvariant();

                if (extentStatus is "maintenance" or "evacuate" or "sealed")
                {
                    findings.Add(new Finding(Severity.Warning, $"sobr:{sobrName}/extent:{extentName}",
                        $"SOBR extent in {extentStatus} mode",
                        new Dictionary<string, object> { ["sobr"] = sobrName, ["extent"] = extentName, ["status"] = extentStatus }));
                }
                else if (extentStatus == "ressyncrequired")
                {
                    findings.Add(new Finding(Severity.Critical, $"sobr:{sobrName}/extent:{extentName}",
                        "SOBR extent requires resync",
                        new Dictionary<string, object> { ["sobr"] = sobrName, ["extent"] = extentName, ["status"] = extentStatus }));
                }
                else
                {
                    findings.Add(new Finding(Severity.Ok, $"sobr:{sobrName}/extent:{extentName}",
                        $"SOBR extent healthy ({extentStatus})",
                        new Dictionary<string, object> { ["sobr"] = sobrName, ["extent"] = extentName, ["status"] = extentStatus }));
                }
            }

            // Capacity tier
            var capEnabled = capTier.GetApiBool("enabled", "Enabled", false);
            findings.Add(new Finding(Severity.Ok, $"sobr:{sobrName}/capacity-tier",
                capEnabled ? "Capacity tier enabled" : "Capacity tier not configured"));

            // Archive tier
            var archEnabled = archiveTier.GetApiBool("enabled", "Enabled", false);
            if (archEnabled)
            {
                findings.Add(new Finding(Severity.Ok, $"sobr:{sobrName}/archive-tier",
                    "Archive tier enabled"));
            }

            // Metrics
            findings.Add(new Finding(Severity.Ok, $"sobr:{sobrName}", "SOBR extent count metric",
                metricName: "veeam_sobr_extent_count", metricValue: perfExtents.Count));

            Logger.Information("SOBR '{SobrName}': {ExtentCount} performance extents, capacity_tier={CapEnabled}, archive_tier={ArchEnabled}",
                sobrName, perfExtents.Count, capEnabled ? "enabled" : "disabled", archEnabled ? "enabled" : "disabled");
        }

        if (sobrs.Count > 0)
            Logger.Information("Checked {Count} scale-out backup repositories", sobrs.Count);

        // --- Overall severity ---
        var overall = Severity.Ok;
        foreach (var f in findings)
        {
            if (f.Severity.Rank() > overall.Rank())
                overall = f.Severity;
        }

        var duration = (int)sw.ElapsedMilliseconds;
        return new MonitorResult(Type, DateTime.UtcNow, duration, overall, findings, errors: errors);
    }
}
