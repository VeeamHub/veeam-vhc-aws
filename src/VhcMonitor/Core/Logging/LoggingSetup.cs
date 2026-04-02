using Serilog;
using Serilog.Events;
using VhcMonitor.Core.Config;

namespace VhcMonitor.Core.Logging;

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
        var logFile = logCfg.Get("file", "./vhc-monitor.log");
        var rotationKeep = logCfg.Get("rotation_keep", 30);
        var consoleEnabled = logCfg.Get("console", true);

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
            .Enrich.WithProperty("Application", "vhc-monitor");

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
        catch (Exception)
        {
            // Fall back to console only if file can't be opened
        }

        // Console sink
        if (consoleEnabled && Console.Error != null)
        {
            logConfig = logConfig.WriteTo.Console(
                standardErrorFromLevel: LogEventLevel.Verbose,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss} [{Level,-8:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}");
        }

        Log.Logger = logConfig.CreateLogger();
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
