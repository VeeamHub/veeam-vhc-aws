using System.CommandLine;
using System.Text.Json;
using VeeamVhcAws.Commands;
using Xunit;

namespace VeeamVhcAws.Tests.Commands;

/// <summary>
/// `captures suppress` must find a key that exists only under findings (for example a state
/// file written before captured_errors existed). The web Alerts page already accepts both.
/// </summary>
[Collection("console")]
public class CapturesSuppressTests : IDisposable
{
    private const string FindingOnlyKey = "0123456789abcdef0123456789abcdef";
    private const string CapturedKey = "fedcba9876543210fedcba9876543210";

    private readonly string _dir;
    private readonly string _configPath;
    private readonly string _statePath;

    public CapturesSuppressTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "vhc-captures-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _configPath = Path.Combine(_dir, "veeam-vhc-aws.yaml");
        _statePath = Path.Combine(_dir, "veeam-vhc-aws-state.json");
        File.WriteAllText(_statePath, $$"""
            {
              "findings": {
                "{{FindingOnlyKey}}": { "resource": "[s1] repo:a", "message": "Repository immutability disabled", "severity": "critical" }
              },
              "captured_errors": {
                "{{CapturedKey}}": { "resource": "[s1] repo:b", "text": "Repository is low on space", "severity": "warning" }
              },
              "suppressions": {}
            }
            """);
    }

    public void Dispose()
    {
        Environment.ExitCode = 0;
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private int Suppress(string prefix)
    {
        Environment.ExitCode = 0;
        CapturesCommand.Create().Invoke(new[] { "suppress", prefix, "-c", _configPath });
        return Environment.ExitCode;
    }

    private JsonElement Suppressions()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(_statePath));
        return doc.RootElement.GetProperty("suppressions").Clone();
    }

    [Fact]
    public void Suppress_FindsAKeyThatIsOnlyUnderFindings()
    {
        Assert.Equal(0, Suppress(FindingOnlyKey[..8]));
        Assert.True(Suppressions().TryGetProperty(FindingOnlyKey, out _));
    }

    [Fact]
    public void Suppress_StillFindsACapturedKey()
    {
        Assert.Equal(0, Suppress(CapturedKey[..8]));
        Assert.True(Suppressions().TryGetProperty(CapturedKey, out _));
    }

    [Fact]
    public void Suppress_UnknownPrefix_FailsAndWritesNothing()
    {
        Assert.Equal(1, Suppress("99999999"));
        Assert.Empty(Suppressions().EnumerateObject());
    }
}
