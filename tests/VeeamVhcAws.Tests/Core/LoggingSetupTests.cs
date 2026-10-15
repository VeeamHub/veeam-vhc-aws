using Serilog;
using Xunit;
using VeeamVhcAws.Core.Logging;

namespace VeeamVhcAws.Tests.Core;

[Collection("console")]
public class LoggingSetupTests
{
    [Fact]
    public void UnwritableLog_FallsBackInsideAnAllowedRoot()
    {
        var root = Directory.CreateTempSubdirectory("vhc-log-in-").FullName;
        var configDir = Path.Combine(root, "config");
        var dataDir = Path.Combine(root, "data");
        Directory.CreateDirectory(configDir);
        Directory.CreateDirectory(dataDir);
        var blocker = Path.Combine(dataDir, "not-a-directory");
        File.WriteAllText(blocker, "x");
        var configured = Path.Combine(blocker, "veeam-vhc-aws.log");
        var configPath = Path.Combine(configDir, "veeam-vhc-aws.yaml");
        File.WriteAllText(configPath, "global:\n  data_directory: '" + dataDir + "'\n");
        var marker = "inside-" + Guid.NewGuid().ToString("N");
        var previous = Log.Logger;
        try
        {
            LoggingSetup.Setup(ConfigWith(configured, dataDir), configPath);
            Log.Information(marker);
            Log.CloseAndFlush();

            var inside = ReadText(configDir) + ReadText(dataDir);
            Assert.Contains(marker, inside);
            Assert.DoesNotContain(marker, ReadEscapeText());
        }
        finally
        {
            Log.Logger = previous;
            Log.CloseAndFlush();
            Directory.Delete(root, recursive: true);
            DeleteMarker(marker);
        }
    }

    [Fact]
    public void UnwritableAllowedRoots_SkipsTheFileSink()
    {
        var root = Directory.CreateTempSubdirectory("vhc-log-skip-").FullName;
        var configDir = Path.Combine(root, "config");
        var dataDir = Path.Combine(root, "data");
        Directory.CreateDirectory(configDir);
        Directory.CreateDirectory(dataDir);
        File.WriteAllText(Path.Combine(configDir, "logs"), "not-a-directory");
        File.WriteAllText(Path.Combine(dataDir, "logs"), "not-a-directory");
        var blocker = Path.Combine(dataDir, "not-a-directory");
        File.WriteAllText(blocker, "x");
        var configured = Path.Combine(blocker, "veeam-vhc-aws.log");
        var configPath = Path.Combine(configDir, "veeam-vhc-aws.yaml");
        File.WriteAllText(configPath, "global:\n  data_directory: '" + dataDir + "'\n");
        var marker = "skip-" + Guid.NewGuid().ToString("N");
        var previous = Log.Logger;
        try
        {
            LoggingSetup.Setup(ConfigWith(configured, dataDir), configPath);
            Log.Information(marker);
            Log.CloseAndFlush();

            Assert.DoesNotContain(marker, ReadEscapeText());
            Assert.DoesNotContain(marker, ReadText(configDir) + ReadText(dataDir));
        }
        finally
        {
            Log.Logger = previous;
            Log.CloseAndFlush();
            Directory.Delete(root, recursive: true);
            DeleteMarker(marker);
        }
    }

    private static Dictionary<string, object> ConfigWith(string file, string dataDir) =>
        new()
        {
            ["global"] = new Dictionary<string, object>
            {
                ["data_directory"] = dataDir,
                ["logging"] = new Dictionary<string, object>
                {
                    ["file"] = file,
                    ["console"] = false,
                    ["level"] = "INFO",
                },
            },
        };

    private static string ReadText(string directory)
    {
        if (!Directory.Exists(directory))
            return "";
        return string.Join("\n", Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
            .Select(File.ReadAllText));
    }

    private static string ReadEscapeText()
    {
        var roots = new List<string> { Path.Combine(Path.GetTempPath(), "VHC") };
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(local))
            roots.Add(Path.Combine(local, "VHC"));
        return string.Join("\n", roots.Select(ReadText));
    }

    private static void DeleteMarker(string marker)
    {
        foreach (var directory in new[]
        {
            Path.Combine(Path.GetTempPath(), "VHC"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VHC"),
        })
        {
            if (!Directory.Exists(directory))
                continue;
            foreach (var file in Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
            {
                if (File.ReadAllText(file).Contains(marker, StringComparison.Ordinal))
                    File.Delete(file);
            }
        }
    }

    [Fact]
    public void Setup_UnwritableFile_WritesTheFallbackFile()
    {
        var root = Directory.CreateTempSubdirectory("vhc-log-").FullName;
        var blocker = Path.Combine(root, "not-a-directory");
        File.WriteAllText(blocker, "x");
        var configured = Path.Combine(blocker, "veeam-vhc-aws.log");
        var previous = Log.Logger;
        try
        {
            LoggingSetup.Setup(new Dictionary<string, object>
            {
                ["global"] = new Dictionary<string, object>
                {
                    ["logging"] = new Dictionary<string, object>
                    {
                        ["file"] = configured,
                        ["console"] = false,
                        ["level"] = "INFO",
                    },
                },
            }, Path.Combine(root, "veeam-vhc-aws.yaml"));
            Log.Information("fallback-proof");
            Log.CloseAndFlush();

            var dir = Path.Combine(root, "logs");
            var files = Directory.Exists(dir) ? Directory.GetFiles(dir) : Array.Empty<string>();
            var text = string.Join("\n", files.Select(File.ReadAllText));
            Assert.Contains("fallback-proof", text);
            Assert.Contains(configured, text);
        }
        finally
        {
            Log.Logger = previous;
            Log.CloseAndFlush();
            Directory.Delete(root, recursive: true);
        }
    }
}
