using Serilog;
using Xunit;
using VeeamVhcAws.Core.Logging;

namespace VeeamVhcAws.Tests.Core;

public class LoggingSetupTests
{
    [Fact]
    public void Setup_UnwritableFile_WritesTheFallbackFile()
    {
        var root = Directory.CreateTempSubdirectory("vhc-log-").FullName;
        var blocker = Path.Combine(root, "not-a-directory");
        File.WriteAllText(blocker, "x");
        var configured = Path.Combine(blocker, "veeam-vhc-aws.log");
        var fallbackRoot = Path.Combine(root, "fallback");
        var previous = Log.Logger;
        LoggingSetup.FallbackDirectoryOverride = fallbackRoot;
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
            });
            Log.Information("fallback-proof");
            Log.CloseAndFlush();

            var dir = Path.Combine(fallbackRoot, "VHC", "logs");
            var files = Directory.Exists(dir) ? Directory.GetFiles(dir) : Array.Empty<string>();
            Assert.NotEmpty(files);
            var text = string.Join("\n", files.Select(File.ReadAllText));
            Assert.Contains("fallback-proof", text);
            Assert.Contains(configured, text);
        }
        finally
        {
            LoggingSetup.FallbackDirectoryOverride = null;
            Log.Logger = previous;
            Log.CloseAndFlush();
            Directory.Delete(root, recursive: true);
        }
    }
}
