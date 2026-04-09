using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Serilog;
using VeeamVhcAws.Core.Config;
using VeeamVhcAws.Core.Models;
using VeeamVhcAws.Core.Patterns;
using VeeamVhcAws.Infrastructure;

namespace VeeamVhcAws.Monitors;

public class RetentionMonitor : IMonitor
{
    private static readonly ILogger Logger = Log.ForContext<RetentionMonitor>();
    private readonly Dictionary<string, object> _config;
    private List<Regex>? _cachedExcludeSessionErrors;

    public MonitorType Type => MonitorType.Retention;
    public IReadOnlyList<string> RequiredConnections => new[] { "vbr" };

    public RetentionMonitor(Dictionary<string, object> config)
    {
        _config = config;
    }

    private Dictionary<string, object> GetConfig() => _config.GetSection("retention");

    public MonitorResult Run(ServerContext serverContext, PatternEngine? patternEngine)
    {
        var sw = Stopwatch.StartNew();
        var findings = new List<Finding>();
        var errors = new List<string>();
        var cfg = GetConfig();
        var thresholds = cfg.GetSection("thresholds");
        var overageMultiplier = thresholds.Get("overage_multiplier", 1.5);
        var maxAgeMultiplier = thresholds.Get("max_age_multiplier", 1.5);
        var orphanDetection = thresholds.Get("orphan_detection", true);

        var excludeBackups = new HashSet<string>();
        if (cfg.TryGetValue("exclude_backups", out var eb) && eb is List<object> ebList)
            foreach (var item in ebList)
                excludeBackups.Add(item.ToString() ?? "");

        var client = serverContext.VbrClient!;

        // --- 1. Get jobs ---
        List<Dictionary<string, object>> jobs;
        try { jobs = client.GetJobs(); }
        catch (Exception e) { errors.Add($"Failed to fetch jobs: {e.Message}"); jobs = new(); }

        var jobMap = new Dictionary<string, Dictionary<string, object>>();
        foreach (var job in jobs)
        {
            var jobId = job.GetApiString("id", "Id", "");
            var jobName = job.GetApiString("name", "Name", "unknown");
            var storage = job.GetSection("storage").Count > 0 ? job.GetSection("storage") : job.GetSection("Storage");
            var retentionPolicy = storage.GetSection("retentionPolicy");
            var retentionType = retentionPolicy.Get("type", "cycles").ToLowerInvariant();
            var quantity = retentionPolicy.GetApiInt("quantity", "Quantity", 14);

            var gfsSec = storage.GetSection("gfsPolicy");
            bool jobGfs = false;
            if (gfsSec.TryGetValue("isEnabled", out var gisv))
                jobGfs = gisv is bool gb ? gb : gisv?.ToString() == "True";

            jobMap[jobId] = new Dictionary<string, object>
            {
                ["name"] = jobName,
                ["retention_type"] = retentionType,
                ["quantity"] = quantity,
                ["gfs_enabled"] = jobGfs,
                ["gfs_weekly"] = jobGfs ? gfsSec.GetSection("weekly").GetApiInt("keepForNumberOfWeeks", "KeepForNumberOfWeeks", 0) : 0,
                ["gfs_monthly"] = jobGfs ? gfsSec.GetSection("monthly").GetApiInt("keepForNumberOfMonths", "KeepForNumberOfMonths", 0) : 0,
                ["gfs_yearly"] = jobGfs ? gfsSec.GetSection("yearly").GetApiInt("keepForNumberOfYears", "KeepForNumberOfYears", 0) : 0,
            };
        }

        // --- 2. Get backups ---
        List<Dictionary<string, object>> backups;
        try { backups = client.GetBackups(); }
        catch (Exception e) { errors.Add($"Failed to fetch backups: {e.Message}"); backups = new(); }

        var backupMap = new Dictionary<string, Dictionary<string, object>>();
        foreach (var backup in backups)
        {
            var backupId = backup.GetApiString("id", "Id", "");
            backupMap[backupId] = new Dictionary<string, object>
            {
                ["job_id"] = backup.GetApiString("jobId", "JobId", ""),
                ["name"] = backup.GetApiString("name", "Name", "unknown"),
                ["policy_id"] = backup.GetApiString("policyUniqueId", "PolicyUniqueId", ""),
                ["platform"] = backup.GetApiString("platformName", "PlatformName", ""),
            };
        }

        // --- 3. Get restore points (paginated) ---
        var allRestorePoints = new List<Dictionary<string, object>>();
        int offset = 0;
        int limit = 500;
        while (true)
        {
            List<Dictionary<string, object>> batch;
            try { batch = client.GetRestorePoints(limit, offset); }
            catch (Exception e)
            {
                errors.Add($"Failed to fetch restore points at offset {offset}: {e.Message}");
                break;
            }
            if (batch.Count == 0) break;
            allRestorePoints.AddRange(batch);
            if (batch.Count < limit) break;
            offset += limit;
        }

        // Group restore points by VM/backup object
        var vmPoints = new Dictionary<string, List<Dictionary<string, object>>>();
        foreach (var rp in allRestorePoints)
        {
            var vmName = rp.GetApiString("name", "Name", "");
            if (string.IsNullOrEmpty(vmName))
                vmName = rp.GetApiString("vmName", "VmName", "unknown");
            if (!vmPoints.ContainsKey(vmName))
                vmPoints[vmName] = new List<Dictionary<string, object>>();
            vmPoints[vmName].Add(rp);
        }

        // --- 4. Check retention compliance ---
        var now = DateTime.UtcNow;
        var activeJobIds = new HashSet<string>(jobMap.Keys);

        foreach (var (vmName, points) in vmPoints)
        {
            // Parse and sort by creation time
            foreach (var p in points)
            {
                var ct = p.GetApiString("creationTime", "CreationTime", "");
                if (!string.IsNullOrEmpty(ct))
                {
                    try
                    {
                        p["_parsed_time"] = DateTime.Parse(ct.Replace("Z", "+00:00"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
                    }
                    catch { p["_parsed_time"] = now; }
                }
                else
                {
                    p["_parsed_time"] = now;
                }
            }
            points.Sort((a, b) => ((DateTime)a["_parsed_time"]).CompareTo((DateTime)b["_parsed_time"]));

            // Find parent job via backup
            var backupId = points[0].GetApiString("backupId", "BackupId", "");
            var parentBackup = backupMap.GetValueOrDefault(backupId, new Dictionary<string, object>());
            var parentJobId = parentBackup.GetValueOrDefault("job_id", "")?.ToString() ?? "";
            var jobInfo = jobMap.GetValueOrDefault(parentJobId);

            if (jobInfo == null) continue;

            var retentionType = jobInfo.GetValueOrDefault("retention_type", "cycles")?.ToString() ?? "cycles";
            var quantity = Convert.ToInt32(jobInfo.GetValueOrDefault("quantity", 14));
            bool gfsEnabled = jobInfo.GetValueOrDefault("gfs_enabled", false) is bool gb2 && gb2;
            int gfsWeekly = Convert.ToInt32(jobInfo.GetValueOrDefault("gfs_weekly", 0));
            int gfsMonthly = Convert.ToInt32(jobInfo.GetValueOrDefault("gfs_monthly", 0));
            int gfsYearly = Convert.ToInt32(jobInfo.GetValueOrDefault("gfs_yearly", 0));
            int actualCount = points.Count;

            if (retentionType == "cycles")
            {
                var baseCycles = quantity;
                var expectedMax = baseCycles + gfsWeekly + gfsMonthly + gfsYearly;
                var threshold = (int)(expectedMax * overageMultiplier);

                if (actualCount > threshold)
                {
                    var severity = actualCount > expectedMax * 2 ? Severity.Critical : Severity.Warning;
                    findings.Add(new Finding(severity, $"vm:{vmName}",
                        $"Retention overage: {actualCount} restore points vs {expectedMax} expected",
                        new Dictionary<string, object>
                        {
                            ["actual"] = actualCount, ["expected"] = expectedMax,
                            ["threshold"] = threshold, ["job"] = jobInfo["name"],
                            ["gfs"] = gfsEnabled,
                        }));
                }
                else
                {
                    findings.Add(new Finding(Severity.Ok, $"vm:{vmName}",
                        $"Retention OK: {actualCount}/{expectedMax} points"));
                }

                // Metrics
                findings.Add(new Finding(Severity.Ok, $"vm:{vmName}", "actual points metric",
                    metricName: "veeam_retention_actual_points", metricValue: actualCount));
                findings.Add(new Finding(Severity.Ok, $"vm:{vmName}", "expected points metric",
                    metricName: "veeam_retention_expected_points", metricValue: expectedMax));
                findings.Add(new Finding(Severity.Ok, $"vm:{vmName}", "violation metric",
                    metricName: "veeam_retention_violation", metricValue: actualCount > threshold ? 1.0 : 0.0));
            }
            else // days-based
            {
                var retainDays = quantity;
                var expectedMax = retainDays + gfsWeekly + gfsMonthly + gfsYearly;
                var threshold = (int)(expectedMax * overageMultiplier);

                if (actualCount > threshold)
                {
                    var severity = actualCount > expectedMax * 2 ? Severity.Critical : Severity.Warning;
                    findings.Add(new Finding(severity, $"vm:{vmName}",
                        $"Retention overage: {actualCount} restore points vs {expectedMax} expected",
                        new Dictionary<string, object>
                        {
                            ["actual"] = actualCount,
                            ["expected"] = expectedMax,
                            ["threshold"] = threshold,
                            ["job"] = jobInfo["name"],
                            ["gfs"] = gfsEnabled,
                        }));
                }
                else
                {
                    findings.Add(new Finding(Severity.Ok, $"vm:{vmName}",
                        $"Retention OK: {actualCount}/{expectedMax} points"));
                }

                findings.Add(new Finding(Severity.Ok, $"vm:{vmName}", "actual points metric",
                    metricName: "veeam_retention_actual_points", metricValue: actualCount));
                findings.Add(new Finding(Severity.Ok, $"vm:{vmName}", "expected points metric",
                    metricName: "veeam_retention_expected_points", metricValue: expectedMax));
                findings.Add(new Finding(Severity.Ok, $"vm:{vmName}", "violation metric",
                    metricName: "veeam_retention_violation", metricValue: actualCount > threshold ? 1.0 : 0.0));
            }
        }

        // --- 5. Orphan detection ---
        int orphanCount = 0;
        if (orphanDetection)
        {
            foreach (var (backupId, backupInfo) in backupMap)
            {
                var jobId = backupInfo.GetValueOrDefault("job_id", "")?.ToString() ?? "";
                if (string.IsNullOrEmpty(jobId)) continue;
                if (activeJobIds.Contains(jobId)) continue;
                if (!string.IsNullOrEmpty(backupInfo.GetValueOrDefault("policy_id", "")?.ToString())) continue;
                var bName = backupInfo.GetValueOrDefault("name", "unknown")?.ToString() ?? "unknown";
                if (excludeBackups.Contains(bName)) continue;

                var orphanPoints = allRestorePoints
                    .Where(rp => rp.GetApiString("backupId", "BackupId", "") == backupId)
                    .ToList();
                var rpCount = orphanPoints.Count;
                var workloadNames = orphanPoints
                    .Select(rp =>
                    {
                        var n = rp.GetApiString("name", "Name", "");
                        if (string.IsNullOrEmpty(n)) n = rp.GetApiString("vmName", "VmName", "");
                        // NAS/unstructured restore points include ordinal suffixes like " Id: 10"
                        // (e.g. "\\syn01\docker Id: 10") — strip them to surface the actual workload path.
                        if (n.StartsWith(@"\\", StringComparison.Ordinal))
                            n = Regex.Replace(n, @"\s+Id:\s*\d+$", "", RegexOptions.IgnoreCase);
                        return n;
                    })
                    .Where(n => !string.IsNullOrEmpty(n))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(n => n)
                    .ToList();
                var workloadList = workloadNames.Count > 0 ? string.Join(", ", workloadNames) : "unknown";
                orphanCount++;
                findings.Add(new Finding(Severity.Warning, $"backup:{bName}",
                    $"Orphaned backup '{bName}': {rpCount} restore points, no active job. Workloads: {workloadList}",
                    new Dictionary<string, object>
                    {
                        ["backup_id"] = backupId, ["job_id"] = jobId, ["restore_point_count"] = rpCount,
                        ["workloads"] = workloadNames,
                    }));
            }

            findings.Add(new Finding(Severity.Ok, "orphan-detection", "orphaned backups metric",
                metricName: "veeam_retention_orphaned_backups", metricValue: orphanCount));
        }

        // --- 6. Retention session failure detection ---
        var sessionLookbackHours = cfg.Get("session_lookback_hours", 24);

        var defaultSessionTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "retention", "backupretention", "deletebackup",
        };
        if (cfg.TryGetValue("session_types", out var stVal) && stVal is List<object> stList)
        {
            defaultSessionTypes = new HashSet<string>(
                stList.Select(x => x.ToString() ?? "").Where(x => x.Length > 0),
                StringComparer.OrdinalIgnoreCase);
        }

        var excludeJobs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (cfg.TryGetValue("exclude_jobs", out var ejVal) && ejVal is List<object> ejList)
            foreach (var item in ejList)
                excludeJobs.Add(item.ToString() ?? "");

        _cachedExcludeSessionErrors ??= BuildExcludeSessionErrors(cfg);
        var excludeSessionErrors = _cachedExcludeSessionErrors;

        List<Dictionary<string, object>> sessions;
        try { sessions = client.GetSessions(lookbackHours: sessionLookbackHours) ?? new(); }
        catch (Exception e) { errors.Add($"Failed to fetch sessions: {e.Message}"); sessions = new(); }

        int retentionSessionFailures = 0;
        var sessionIssues = new Dictionary<(string, string, string), (int Count, Severity Severity, string ErrorText, string SessionName)>();

        foreach (var session in sessions)
        {
            var sessionType = session.GetApiString("sessionType", "type", "");
            if (string.IsNullOrEmpty(sessionType))
                sessionType = session.GetApiString("type", "Type", "");
            sessionType = sessionType.ToLowerInvariant();

            if (!defaultSessionTypes.Contains(sessionType))
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

            var sessionName = session.GetApiString("name", "Name", "unknown");

            // Filter: exclude by job name
            if (excludeJobs.Any(ej => sessionName.Contains(ej, StringComparison.OrdinalIgnoreCase)))
                continue;

            // Filter: exclude by error pattern
            if (excludeSessionErrors.Any(r => r.IsMatch(errorText)))
                continue;

            retentionSessionFailures++;
            var severity = resultStatus == "failed" ? Severity.Critical : Severity.Warning;

            string patternCat = "";
            if (patternEngine != null && !string.IsNullOrEmpty(errorText))
            {
                var matched = patternEngine.Classify(errorText);
                if (matched != null)
                {
                    patternCat = matched.Category;
                    severity = Severity.Critical;
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

        foreach (var ((sessionType, resultStatus, patternCat), info) in sessionIssues)
        {
            var countSuffix = info.Count > 1 ? $" ({info.Count}x in last {sessionLookbackHours}h)" : "";
            var errorDetail = !string.IsNullOrEmpty(info.ErrorText) ? info.ErrorText : "check VBR console for details";

            string msg = !string.IsNullOrEmpty(patternCat)
                ? $"Retention {patternCat} failure in {sessionType}{countSuffix}: {errorDetail}"
                : $"Retention {sessionType} {resultStatus}{countSuffix}: {errorDetail}";

            findings.Add(new Finding(info.Severity, $"session:{info.SessionName}", msg,
                new Dictionary<string, object>
                {
                    ["session_type"] = sessionType,
                    ["session_name"] = info.SessionName,
                    ["result"] = resultStatus,
                    ["count"] = info.Count,
                    ["lookback_hours"] = sessionLookbackHours,
                    ["error"] = info.ErrorText,
                }));

            Logger.Information("Retention session issue: {Type} {Status} ({Count}x): {Error}",
                sessionType, resultStatus, info.Count, info.ErrorText);
        }

        findings.Add(new Finding(Severity.Ok, "retention-sessions", "retention session failures metric",
            metricName: "veeam_retention_session_failures", metricValue: retentionSessionFailures));

        // --- Overall severity ---
        var overall = Severity.Ok;
        foreach (var f in findings)
            if (f.Severity.Rank() > overall.Rank())
                overall = f.Severity;

        var duration = (int)sw.ElapsedMilliseconds;
        return new MonitorResult(Type, DateTime.UtcNow, duration, overall, findings, errors: errors);
    }

    private static List<Regex> BuildExcludeSessionErrors(Dictionary<string, object> cfg)
    {
        var result = new List<Regex>();
        if (cfg.TryGetValue("exclude_session_errors", out var eseVal) && eseVal is List<object> eseList)
            foreach (var item in eseList)
            {
                var pat = item.ToString() ?? "";
                if (pat.Length > 0)
                    result.Add(new Regex(pat, RegexOptions.IgnoreCase | RegexOptions.Compiled));
            }
        return result;
    }
}
