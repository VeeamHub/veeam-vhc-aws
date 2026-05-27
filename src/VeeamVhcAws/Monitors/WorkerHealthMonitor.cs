using System.Diagnostics;
using System.Globalization;
using Serilog;
using VeeamVhcAws.Core.Config;
using VeeamVhcAws.Core.Models;
using VeeamVhcAws.Core.Patterns;
using VeeamVhcAws.Core.State;
using VeeamVhcAws.Infrastructure;

namespace VeeamVhcAws.Monitors;

public class WorkerHealthMonitor : IMonitor
{
    private static readonly ILogger Logger = Log.ForContext<WorkerHealthMonitor>();
    private readonly Dictionary<string, object> _config;

    public MonitorType Type => MonitorType.WorkerHealth;
    public IReadOnlyList<string> RequiredConnections => new[] { "vbaws" };

    public WorkerHealthMonitor(Dictionary<string, object> config)
    {
        _config = config;
    }

    private Dictionary<string, object> GetConfig() => _config.GetSection("worker_health");

    private static string GetSessionType(Dictionary<string, object> session) =>
        session.GetApiString("sessionType", "type", session.GetApiString("type", "Type", "")).ToLowerInvariant();

    private static string GetSessionState(Dictionary<string, object> session) =>
        session.GetApiString("state", "status", session.GetApiString("status", "Status", "")).ToLowerInvariant();

    private static string GetSessionName(Dictionary<string, object> session) =>
        session.GetApiString("policyName", "name", session.GetApiString("name", "Name", "unknown"));

    private static string CategorizeSession(Dictionary<string, object> session)
    {
        var sessionType = GetSessionType(session);
        if (sessionType.Contains("backup") || sessionType.Contains("policy")) return "backup";
        if (sessionType.Contains("restore") || sessionType.Contains("flr")) return "restore";
        if (sessionType.Contains("retention")) return "retention";
        return "other";
    }

    private string GetErrorText(Dictionary<string, object> session, ServerContext ctx)
    {
        // Try inline fields
        var resultObj = session.GetApi("result", "Result", null);
        if (resultObj is Dictionary<string, object> resultDict)
        {
            var msg = resultDict.GetApiString("message", "Message", "");
            if (!string.IsNullOrEmpty(msg) && msg.ToLowerInvariant() is not ("failed" or "warning" or ""))
                return msg;
        }
        var reason = session.GetApiString("reason", "reason", "");
        if (!string.IsNullOrEmpty(reason) && reason != GetSessionName(session))
            return reason;

        // Fetch session logs for actual error
        var sid = session.GetApiString("id", "Id", "");
        if (!string.IsNullOrEmpty(sid) && ctx.VbawsClient != null)
        {
            try
            {
                var logs = ctx.VbawsClient.GetSessionLogs(sid);
                var failedLogs = logs
                    .Where(log => log.GetApiString("status", "Status", "").Equals("failed", StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrEmpty(log.GetApiString("title", "Title", ""))
                        && !log.GetApiString("title", "Title", "").Contains("session finished", StringComparison.OrdinalIgnoreCase))
                    .Select(log => log.GetApiString("title", "Title", ""))
                    .ToList();

                if (failedLogs.Count > 0)
                    return failedLogs[0];
            }
            catch (Exception e)
            {
                Logger.Debug("Could not fetch logs for session {SessionId}: {Error}", sid[..Math.Min(8, sid.Length)], e.Message);
            }
        }

        return resultObj?.ToString() ?? "";
    }

    private static List<Dictionary<string, object>> FilterByLookback(
        List<Dictionary<string, object>> sessions, DateTime fromDt)
    {
        var filtered = new List<Dictionary<string, object>>();
        foreach (var s in sessions)
        {
            var ct = s.GetApiString("creationTime", "CreationTime", "");
            if (string.IsNullOrEmpty(ct))
            {
                filtered.Add(s);
                continue;
            }
            try
            {
                var sessionTime = DateTime.Parse(ct.Replace("Z", "+00:00"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
                if (sessionTime >= fromDt)
                    filtered.Add(s);
            }
            catch
            {
                filtered.Add(s);
            }
        }
        return filtered;
    }

    public MonitorResult Run(ServerContext serverContext, PatternEngine? patternEngine, FindingState? findingState = null)
    {
        var sw = Stopwatch.StartNew();
        var findings = new List<Finding>();
        var errors = new List<string>();
        var cfg = GetConfig();
        var thresholds = cfg.GetSection("thresholds");
        var lookbackHours = cfg.Get("lookback_hours", 24);
        var warningRate = thresholds.Get("session_failure_rate_warning", 0.1);
        var criticalRate = thresholds.Get("session_failure_rate_critical", 0.3);
        var zeroDeletedWarning = thresholds.Get("zero_deleted_items_warning", true);
        var recurringThreshold = thresholds.Get("recurring_failure_threshold", 3);

        // --- 1. Session collection ---
        var now = DateTime.UtcNow;
        var fromDt = now.AddHours(-lookbackHours);

        List<Dictionary<string, object>> sessions;
        try
        {
            sessions = serverContext.VbawsClient!.GetSessions(fromDt, now);
        }
        catch (Exception e)
        {
            errors.Add($"Failed to fetch VBAWS sessions: {e.Message}");
            sessions = new();
        }

        sessions = FilterByLookback(sessions, fromDt);
        Logger.Information("VBAWS sessions in lookback window: {Count} (lookback={LookbackHours}h)", sessions.Count, lookbackHours);

        // Categorize sessions
        var categorized = new Dictionary<string, List<Dictionary<string, object>>>();
        foreach (var session in sessions)
        {
            var cat = CategorizeSession(session);
            if (!categorized.ContainsKey(cat))
                categorized[cat] = new List<Dictionary<string, object>>();
            categorized[cat].Add(session);
        }

        // --- 2. Overall session health ---
        foreach (var (sessionType, typeSessions) in categorized)
        {
            var total = typeSessions.Count;
            var failedSessions = typeSessions.Where(s => GetSessionState(s) == "failed").ToList();
            var failed = failedSessions.Count;
            var failureRate = total > 0 ? (double)failed / total : 0.0;

            // Per-policy latest-only
            var byPolicy = new Dictionary<string, List<Dictionary<string, object>>>();
            foreach (var s in typeSessions)
            {
                var policy = GetSessionName(s);
                if (!byPolicy.ContainsKey(policy))
                    byPolicy[policy] = new List<Dictionary<string, object>>();
                byPolicy[policy].Add(s);
            }

            foreach (var (policy, policySessions) in byPolicy)
            {
                policySessions.Sort((a, b) =>
                    string.Compare(
                        b.GetApiString("creationTime", "CreationTime", ""),
                        a.GetApiString("creationTime", "CreationTime", ""),
                        StringComparison.Ordinal));

                var latest = policySessions[0];
                if (GetSessionState(latest) != "failed") continue;

                var errorText = GetErrorText(latest, serverContext);
                ErrorPattern? matched = null;
                if (patternEngine != null && !string.IsNullOrEmpty(errorText))
                    matched = patternEngine.Classify(errorText);

                var findingSev = matched != null ? Severity.Critical : Severity.Warning;
                var findingMsg = matched?.Message ?? (errorText.Length > 150 ? errorText[..150] : errorText);
                if (string.IsNullOrEmpty(findingMsg)) findingMsg = "failed — check VBAWS console";

                var details = new Dictionary<string, object>
                {
                    ["error"] = errorText,
                    ["policy"] = policy,
                    ["category"] = matched?.Category ?? "unclassified",
                    ["session_time"] = latest.GetApiString("creationTime", "CreationTime", ""),
                };
                if (matched?.Remediation is { Length: > 0 })
                    details["remediation"] = matched.Remediation;

                findings.Add(new Finding(findingSev, $"failed:{policy}", findingMsg, details));
            }

            // Metrics
            findings.Add(new Finding(Severity.Ok, $"session-type:{sessionType}", "session total metric",
                metricName: "veeam_vbaws_session_total", metricValue: total));
            findings.Add(new Finding(Severity.Ok, $"session-type:{sessionType}", "session failed metric",
                metricName: "veeam_vbaws_session_failed", metricValue: failed));
            findings.Add(new Finding(Severity.Ok, $"session-type:{sessionType}", "session failure rate metric",
                metricName: "veeam_vbaws_session_failure_rate", metricValue: failureRate));
        }

        // --- 3. Retention session deep inspection ---
        var retentionSessions = categorized.GetValueOrDefault("retention", new List<Dictionary<string, object>>());
        int subnetExhaustionCount = 0;
        double workerHealthInferred = 1.0;
        int retentionDeletedTotal = 0;

        foreach (var session in retentionSessions)
        {
            var status = GetSessionState(session);

            if (status == "failed")
            {
                var errorText = GetErrorText(session, serverContext);
                ErrorPattern? matched = null;
                if (patternEngine != null)
                    matched = patternEngine.Classify(errorText);

                var backupName = GetSessionName(session);

                if (matched != null && matched.Category == "network")
                {
                    subnetExhaustionCount++;
                    workerHealthInferred = 0.0;
                    var extracted = patternEngine != null ? patternEngine.Extract(errorText, matched) : new();
                    var subnetId = extracted.GetValueOrDefault("subnet_id", "unknown");
                    findings.Add(new Finding(Severity.Critical, $"retention:{backupName}",
                        $"Subnet IP exhaustion in {subnetId}",
                        new Dictionary<string, object>
                        {
                            ["subnet_id"] = subnetId, ["backup_name"] = backupName,
                            ["log_path"] = matched.LogHint, ["remediation"] = matched.Remediation,
                            ["error"] = errorText,
                        }));
                }
                else if (matched != null && matched.Category == "credential")
                {
                    workerHealthInferred = 0.0;
                    findings.Add(new Finding(Severity.Critical, $"retention:{backupName}",
                        "Expired AWS credentials",
                        new Dictionary<string, object> { ["error"] = errorText }));
                }
                else
                {
                    workerHealthInferred = 0.0;
                    findings.Add(new Finding(Severity.Critical, $"retention:{backupName}",
                        "Retention session failed — workers may not be deploying",
                        new Dictionary<string, object> { ["error"] = errorText }));
                }
            }
            else if (status is "success" or "completed")
            {
                int? deletedItems = null;
                if (session.TryGetValue("deletedItems", out var di) || session.TryGetValue("DeletedItems", out di))
                {
                    deletedItems = Convert.ToInt32(di);
                }
                else if (session.GetSection("details") is { Count: > 0 } details)
                {
                    if (details.TryGetValue("deletedItems", out var ddi))
                        deletedItems = Convert.ToInt32(ddi);
                }

                if (deletedItems.HasValue)
                    retentionDeletedTotal += deletedItems.Value;

                if (zeroDeletedWarning && deletedItems.HasValue && deletedItems.Value == 0)
                {
                    var backupName = GetSessionName(session);
                    findings.Add(new Finding(Severity.Warning, $"retention:{backupName}",
                        "Retention ran but deleted nothing — possible silent failure",
                        new Dictionary<string, object> { ["backup_name"] = backupName }));
                }
            }
        }

        // Retention metrics
        findings.Add(new Finding(Severity.Ok, "retention-analysis", "subnet exhaustion metric",
            metricName: "veeam_vbaws_subnet_exhaustion", metricValue: subnetExhaustionCount));
        findings.Add(new Finding(Severity.Ok, "retention-analysis", "worker health inferred metric",
            metricName: "veeam_vbaws_worker_health_inferred", metricValue: workerHealthInferred));
        findings.Add(new Finding(Severity.Ok, "retention-analysis", "retention deleted items metric",
            metricName: "veeam_vbaws_retention_deleted_items_total", metricValue: retentionDeletedTotal));

        // --- 4. Session gap detection ---
        if (retentionSessions.Count == 0)
        {
            findings.Add(new Finding(Severity.Warning, "retention-gap",
                $"No retention sessions in last {lookbackHours}h",
                new Dictionary<string, object> { ["lookback_hours"] = lookbackHours }));
        }

        // --- 5. Failure pattern analysis ---
        var allFailed = sessions.Where(s => GetSessionState(s) == "failed").ToList();
        var categoryCounts = new Dictionary<string, int>();
        var unclassifiedErrors = new Dictionary<string, int>();

        foreach (var session in allFailed)
        {
            var errorText = GetErrorText(session, serverContext);
            ErrorPattern? matched = null;
            if (patternEngine != null)
                matched = patternEngine.Classify(errorText);

            if (matched != null)
            {
                categoryCounts[matched.Category] = categoryCounts.GetValueOrDefault(matched.Category, 0) + 1;
            }
            else
            {
                unclassifiedErrors[errorText] = unclassifiedErrors.GetValueOrDefault(errorText, 0) + 1;
            }
        }

        foreach (var (category, count) in categoryCounts)
        {
            if (count >= recurringThreshold)
            {
                findings.Add(new Finding(Severity.Critical, $"pattern:{category}",
                    $"Recurring {category} failure: {count} occurrences",
                    new Dictionary<string, object> { ["category"] = category, ["count"] = count }));
            }

            findings.Add(new Finding(Severity.Ok, $"pattern:{category}", "failure by category metric",
                metricName: "veeam_vbaws_failure_by_category", metricValue: count));
        }

        foreach (var (errorText, count) in unclassifiedErrors)
        {
            if (count >= 3)
            {
                findings.Add(new Finding(Severity.Warning, "pattern:unclassified",
                    $"Recurring unclassified failure: {count} occurrences",
                    new Dictionary<string, object>
                    {
                        ["error"] = errorText.Length > 200 ? errorText[..200] : errorText,
                        ["count"] = count,
                    }));
            }
        }

        // --- Overall severity ---
        var overall = Severity.Ok;
        foreach (var f in findings)
            if (f.Severity.Rank() > overall.Rank())
                overall = f.Severity;

        var duration = (int)sw.ElapsedMilliseconds;
        return new MonitorResult(Type, DateTime.UtcNow, duration, overall, findings, errors: errors);
    }
}
