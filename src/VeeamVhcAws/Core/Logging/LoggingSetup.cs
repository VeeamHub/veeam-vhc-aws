using Serilog;
using Serilog.Events;
using VeeamVhcAws.Core.Config;

namespace VeeamVhcAws.Core.Logging;

public static class LoggingSetup
{
    private static readonly string[] SensitivePatterns = new[]
    {
        "password", "Password", "access_token", "Authorization", "Bearer", "Basic"
    };

    public static void Setup(Dictionary<string, object> config)
    {
        var globalCfg = config.GetSection("global");
        var logCfg = globalCfg.GetSection("logging");

        var levelName = logCfg.Get("level", "DEBUG").ToUpperInvariant();
        var logFile = logCfg.Get("file", "./veeam-vhc-aws.log");
        var rotationKeep = logCfg.Get("rotation_keep", 30);
        var consoleEnabled = logCfg.Get("console", true);
        var diskWarningPct = logCfg.Get("disk_warning_pct", 20);

        var level = levelName switch
        {
            "DEBUG" => LogEventLevel.Debug,
            "INFO" or "INFORMATION" => LogEventLevel.Information,
            "WARNING" or "WARN" => LogEventLevel.Warning,
            "ERROR" => LogEventLevel.Error,
            "FATAL" => LogEventLevel.Fatal,
            _ => LogEventLevel.Debug,
        };

        var logConfig = new LoggerConfiguration()
            .MinimumLevel.Is(level)
            .Enrich.WithProperty("Application", "veeam-vhc-aws");

        // File sink with daily rolling
        // Serilog inserts the date before the extension (e.g. "app.log" → "app20260402.log").
        // Rewrite the path so there's a dash separator: "app.log" → "app-.log" → "app-20260402.log".
        try
        {
            var logDir = Path.GetDirectoryName(Path.GetFullPath(logFile));
            if (!string.IsNullOrEmpty(logDir))
                Directory.CreateDirectory(logDir);

            var baseName = Path.GetFileNameWithoutExtension(logFile);
            var ext = Path.GetExtension(logFile);
            var rollingPath = Path.Combine(
                Path.GetDirectoryName(logFile) ?? ".",
                baseName + "-" + ext);

            logConfig = logConfig.WriteTo.File(
                rollingPath,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: rotationKeep,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss} [{Level,-8:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}",
                encoding: System.Text.Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[veeam-vhc-aws] WARNING: File logging could not be configured for '{logFile}': {ex.Message}. Falling back to console only.");
        }

        // Console sink
        if (consoleEnabled && Console.Error != null)
        {
            logConfig = logConfig.WriteTo.Console(
                standardErrorFromLevel: LogEventLevel.Verbose,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss} [{Level,-8:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}");
        }

        Log.Logger = logConfig.CreateLogger();

        CheckLogDiskSpace(logFile, diskWarningPct);
    }

    /// <summary>
    /// Checks free space on the log file's drive and emits a warning or critical log on every run.
    /// Warning fires at exactly the threshold; critical fires for every percent below it.
    /// No deduplication — fires on every invocation so the operator cannot miss it.
    /// </summary>
    private static void CheckLogDiskSpace(string logFilePath, int warningPct)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(logFilePath));
            if (string.IsNullOrEmpty(root)) return;

            var drive = new System.IO.DriveInfo(root);
            if (!drive.IsReady || drive.TotalSize == 0) return;

            var freePct = (int)(drive.AvailableFreeSpace * 100L / drive.TotalSize);
            var freeGb = drive.AvailableFreeSpace / (1024.0 * 1024.0 * 1024.0);

            if (freePct < warningPct)
                Log.Error("Disk space CRITICAL on log drive ({Root}): {FreePct}% free ({FreeGb:F1} GB) — below {WarningPct}% warning threshold",
                    root, freePct, freeGb, warningPct);
            else if (freePct == warningPct)
                Log.Warning("Disk space WARNING on log drive ({Root}): {FreePct}% free ({FreeGb:F1} GB) — at {WarningPct}% threshold",
                    root, freePct, freeGb, warningPct);
        }
        catch
        {
            // Best effort — don't block startup if drive info is unavailable
        }
    }

    public static string RedactSensitive(string message)
    {
        // Simple redaction for log output
        var result = message;
        foreach (var pattern in SensitivePatterns)
        {
            var idx = result.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
            while (idx >= 0)
            {
                // Find the value after = or : or space
                var valueStart = result.IndexOfAny(new[] { '=', ':', ' ' }, idx + pattern.Length);
                if (valueStart >= 0 && valueStart < result.Length - 1)
                {
                    valueStart++;
                    // Skip whitespace and quotes
                    while (valueStart < result.Length && (result[valueStart] == ' ' || result[valueStart] == '"' || result[valueStart] == '\''))
                        valueStart++;

                    var valueEnd = valueStart;
                    while (valueEnd < result.Length && result[valueEnd] != ' ' && result[valueEnd] != ',' &&
                           result[valueEnd] != '"' && result[valueEnd] != '\'' && result[valueEnd] != '}' &&
                           result[valueEnd] != ']')
                        valueEnd++;

                    if (valueEnd > valueStart)
                        result = result[..valueStart] + "***REDACTED***" + result[valueEnd..];
                }
                idx = result.IndexOf(pattern, idx + pattern.Length + 14, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) break;
            }
        }
        return result;
    }
}
