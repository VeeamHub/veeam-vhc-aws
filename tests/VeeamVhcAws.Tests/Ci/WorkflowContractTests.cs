using System.Text.RegularExpressions;
using Xunit;

namespace VeeamVhcAws.Tests.Ci;

public abstract record WorkflowFact(string RelativePath);

public sealed record LiteralFact(string RelativePath, string ExpectedLiteral) : WorkflowFact(RelativePath);

public sealed record PinnedThirdPartyUsesFact(string RelativePath, string ExpectedForm) : WorkflowFact(RelativePath);

public static class CiWorkflowContract
{
    public const string TruffleHogPin =
        "trufflesecurity/trufflehog@6f3c981e7b77f235fd2702dd74af25fc4b72bf11 # v3.96.0";

    public const string PinnedUsesForm = @"^[^\s]+ \S+@[0-9a-f]{40} # \S+$";

    public static readonly IReadOnlyList<WorkflowFact> Required =
    [
        new LiteralFact(
            ".github/workflows/release.yml",
            "dotnet test tests/VeeamVhcAws.UiTests/VeeamVhcAws.UiTests.csproj -c Release"),
        new LiteralFact(
            ".github/dependabot.yml",
            """
              - package-ecosystem: nuget
                directory: "/"
                schedule:
                  interval: weekly
                target-branch: dev
            """),
        new LiteralFact(
            ".github/dependabot.yml",
            """
              - package-ecosystem: github-actions
                directory: "/"
                schedule:
                  interval: weekly
                target-branch: dev
            """),
        new LiteralFact(
            ".github/workflows/codeql.yml",
            """
              push:
                branches: [dev]
              pull_request:
                branches: [dev]
            """),
        new LiteralFact(".github/workflows/codeql.yml", "languages: csharp"),
        new LiteralFact(
            ".github/workflows/codeql.yml",
            "dotnet build src/VeeamVhcAws/VeeamVhcAws.csproj -c Release"),
        new LiteralFact(".github/workflows/security.yml", TruffleHogPin),
        new PinnedThirdPartyUsesFact(".github/workflows", PinnedUsesForm),
    ];
}

public class WorkflowContractTests
{
    private static readonly Regex UsesLine = new(
        @"^[ \t]*(?:- )?uses:[ \t]+(?<value>\S+)(?<comment>.*)$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

    public static IEnumerable<object[]> LiteralCases()
    {
        foreach (var fact in CiWorkflowContract.Required.OfType<LiteralFact>())
            yield return [fact.RelativePath, fact.ExpectedLiteral];
    }

    [Theory]
    [MemberData(nameof(LiteralCases))]
    public void WorkflowFile_ContainsRequiredLiteral(string relativePath, string expectedLiteral)
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), relativePath));
        Assert.Contains(expectedLiteral, text.Replace("\r\n", "\n"));
    }

    [Fact]
    public void ThirdPartyUses_AreFullCommitShasWithTagComments()
    {
        var fact = Assert.Single(CiWorkflowContract.Required.OfType<PinnedThirdPartyUsesFact>());
        var lines = ThirdPartyUses(Path.Combine(RepoRoot(), fact.RelativePath));

        Assert.Contains($"security.yml {CiWorkflowContract.TruffleHogPin}", lines);
        var offenders = lines.Where(line => !Regex.IsMatch(line, fact.ExpectedForm)).ToArray();
        Assert.Equal("", string.Join("\n", offenders));
    }

    private static IReadOnlyList<string> ThirdPartyUses(string workflowsDirectory)
    {
        var lines = new List<string>();
        foreach (var file in Directory.EnumerateFiles(workflowsDirectory, "*.yml").OrderBy(Path.GetFileName))
        {
            var text = File.ReadAllText(file).Replace("\r\n", "\n");
            foreach (Match match in UsesLine.Matches(text))
            {
                var value = match.Groups["value"].Value;
                if (value.StartsWith("./", StringComparison.Ordinal)
                    || value.StartsWith("docker://", StringComparison.Ordinal))
                    continue;

                var comment = match.Groups["comment"].Value.TrimEnd();
                lines.Add($"{Path.GetFileName(file)} {value}{comment}");
            }
        }

        return lines;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "VeeamVhcAws.sln"))
                && File.Exists(Path.Combine(dir.FullName, ".github", "dependabot.yml")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException($"repo root not found from {AppContext.BaseDirectory}");
    }
}
