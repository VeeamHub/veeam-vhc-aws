using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;
using VeeamVhcAws.Commands;
using VeeamVhcAws.Infrastructure;

namespace VeeamVhcAws.Tests.Ci;

/// <summary>
/// Config samples that ship with the tool (setup --template fallback, example.yaml, README)
/// are what operators copy. They must teach the same defaults the code uses.
/// </summary>
public class ShippedConfigSamplesTests
{
    private static readonly Regex VerifySslLine = new(@"^\s*#?\s*verify_ssl:\s*(\S+)", RegexOptions.Multiline);

    public static TheoryData<string> Samples => new()
    {
        "setup --template minimal config",
        "config/example.yaml",
        "src/VeeamVhcAws/config/example.yaml",
        "README.md yaml blocks",
    };

    [Theory]
    [MemberData(nameof(Samples))]
    public void Sample_TeachesVerifySslTrue(string sample)
    {
        var text = SampleText(sample);
        var values = VerifySslLine.Matches(text).Select(m => m.Groups[1].Value).ToList();
        Assert.NotEmpty(values);
        Assert.All(values, value => Assert.Equal("true", value));
    }

    [Theory]
    [InlineData("config/example.yaml")]
    [InlineData("src/VeeamVhcAws/config/example.yaml")]
    public void ExampleConfig_StatesTheParallelThresholdTheRunnerUses(string sample)
    {
        var threshold = (int)typeof(MonitorRunner)
            .GetField("ParallelThreshold", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetRawConstantValue()!;
        var text = SampleText(sample);

        Assert.DoesNotContain("Servers >5", text);
        Assert.Contains($"{threshold + 1}+ servers run in parallel", text);
    }

    internal static string SampleText(string sample) => sample switch
    {
        "setup --template minimal config" => (string)typeof(SetupCommand)
            .GetField("MinimalConfig", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetRawConstantValue()!,
        "README.md yaml blocks" => string.Join('\n', YamlBlocks(File.ReadAllText(Path.Combine(RepoRoot(), "README.md")))),
        _ => File.ReadAllText(Path.Combine(RepoRoot(), sample)),
    };

    private static IEnumerable<string> YamlBlocks(string markdown) =>
        Regex.Matches(markdown, @"```ya?ml\r?\n(.*?)```", RegexOptions.Singleline).Select(m => m.Groups[1].Value);

    internal static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "VeeamVhcAws.sln"))
                && File.Exists(Path.Combine(dir.FullName, "README.md")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException($"repo root not found from {AppContext.BaseDirectory}");
    }
}
