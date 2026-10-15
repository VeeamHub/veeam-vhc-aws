using System.CommandLine;
using System.Text.Json;
using Spectre.Console;
using VeeamVhcAws.Commands;
using VeeamVhcAws.Core.Config;
using VeeamVhcAws.Ui;
using Xunit;

namespace VeeamVhcAws.Tests.Commands;

/// <summary>
/// One temp-directory test per critical CLI command, driven through System.CommandLine the
/// way Program wires it. None of these touch the network.
/// </summary>
[Collection("console")]
public class CommandWiringTests : IDisposable
{
    private const string CapturedKey = "aaaaaaaa11111111aaaaaaaa11111111";
    private const string OtherKey = "bbbbbbbb22222222bbbbbbbb22222222";

    private readonly string _dir;
    private readonly IAnsiConsole _priorConsole;
    private readonly StringWriter _ansiOut = new();

    public CommandWiringTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "vhc-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _priorConsole = AnsiConsole.Console;
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.No,
            Out = new AnsiConsoleOutput(_ansiOut),
        });
        console.Profile.Width = 250;
        AnsiConsole.Console = console;
        Environment.ExitCode = 0;
    }

    public void Dispose()
    {
        AnsiConsole.Console = _priorConsole;
        Serilog.Log.CloseAndFlush();
        TerminalCapabilities.ResetForTesting();
        Environment.ExitCode = 0;
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    // ---- setup ----

    [Fact]
    public void SetupTemplate_WritesTheBundledExampleThatLoadsWithVerifySslOn()
    {
        var path = Path.Combine(_dir, "veeam-vhc-aws.yaml");

        var exit = Quiet(() => SetupCommand.Create().Invoke(new[] { "--template", "-o", path }));

        Assert.Equal(0, exit);
        Assert.True(File.Exists(path));
        var config = ConfigLoader.LoadConfig(path);
        var servers = config.GetListOfSections("servers");
        Assert.NotEmpty(servers);
        Assert.All(servers, s => Assert.True(s.Get("verify_ssl", false)));
        Assert.Contains(config.GetListOfSections("output"), o => o.Get("type", "") == "json_stdout");
    }

    [Fact]
    public void SetupTemplate_LeavesAnExistingFileAlone()
    {
        var path = Path.Combine(_dir, "veeam-vhc-aws.yaml");
        File.WriteAllText(path, "# mine\n");

        Quiet(() => SetupCommand.Create().Invoke(new[] { "--template", "-o", path }));

        Assert.Equal("# mine\n", File.ReadAllText(path));
    }

    [Fact]
    public void Setup_WithoutAnInteractiveTerminal_FallsBackToTheTemplate()
    {
        var path = Path.Combine(_dir, "veeam-vhc-aws.yaml");
        TerminalCapabilities.SetNoInteractive();

        var exit = Quiet(() => SetupCommand.Create().Invoke(new[] { "-o", path }));

        Assert.Equal(0, exit);
        Assert.NotEmpty(ConfigLoader.LoadConfig(path).GetListOfSections("servers"));
    }

    // ---- encrypt-config ----

    [Fact]
    public void EncryptConfig_LongConfigOption_ProtectsThePassword()
    {
        var path = WriteServerConfig("plain-pw");

        var exit = Quiet(() => EncryptConfigCommand.Create().Invoke(new[] { "--config", path }));

        Assert.Equal(0, exit);
        Assert.DoesNotContain("plain-pw", File.ReadAllText(path));
        Assert.Contains("password: \"ENC:", File.ReadAllText(path));
    }

    [Fact]
    public void EncryptConfig_AcceptsTheShortConfigAlias()
    {
        var path = WriteServerConfig("plain-pw");

        var exit = Quiet(() => EncryptConfigCommand.Create().Invoke(new[] { "-c", path }));

        Assert.Equal(0, exit);
        Assert.DoesNotContain("plain-pw", File.ReadAllText(path));
    }

    // ---- all ----

    // `all` binds the process-wide secret-key directory to the config directory the first
    // time it runs, so these use one stable directory that outlives the test instead of _dir.
    private static string AllConfigPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vhc-cli-all");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, Guid.NewGuid().ToString("N") + ".yaml");
    }

    [Fact]
    public void All_WithAnUnparsableConfig_ExitsThree()
    {
        var path = AllConfigPath();
        File.WriteAllText(path, "servers: [unclosed\n");

        Quiet(() => AllCommand.Create().Invoke(new[] { "-c", path }));

        Assert.Equal(3, Environment.ExitCode);
    }

    [Fact]
    public void All_WithNoServers_ExitsZero()
    {
        var path = AllConfigPath();
        File.WriteAllText(path, "global:\n  state_file: ./" + Path.GetFileNameWithoutExtension(path) + "-state.json\n  logging:\n    console: false\n    file: ./vhc.log\nservers: []\n");

        Quiet(() => AllCommand.Create().Invoke(new[] { "-c", path }));

        Assert.Equal(0, Environment.ExitCode);
    }

    // ---- captures ----

    [Fact]
    public void CapturesList_ShowsUnsuppressedEntries()
    {
        var config = WriteCapturesState();

        Quiet(() => CapturesCommand.Create().Invoke(new[] { "list", "-c", config }));

        var text = _ansiOut.ToString();
        Assert.Contains(CapturedKey[..8], text);
        Assert.Contains(OtherKey[..8], text);
        Assert.DoesNotContain(CapturedKey[..9], text);
    }

    [Fact]
    public void CapturesSuppress_ThenUnsuppress_RoundTripsTheStateFile()
    {
        var config = WriteCapturesState();

        Quiet(() => CapturesCommand.Create().Invoke(new[] { "suppress", CapturedKey[..8], "-c", config }));
        Assert.Equal(0, Environment.ExitCode);
        Assert.True(Suppressions().TryGetProperty(CapturedKey, out _));

        Quiet(() => CapturesCommand.Create().Invoke(new[] { "unsuppress", CapturedKey[..8], "-c", config }));
        Assert.Equal(0, Environment.ExitCode);
        Assert.False(Suppressions().TryGetProperty(CapturedKey, out _));
    }

    [Fact]
    public void CapturesUnsuppress_UnknownPrefix_ExitsOne()
    {
        var config = WriteCapturesState();

        Quiet(() => CapturesCommand.Create().Invoke(new[] { "unsuppress", "99999999", "-c", config }));

        Assert.Equal(1, Environment.ExitCode);
    }

    [Fact]
    public void CapturesList_HidesAnEntryRightAfterCapturesSuppress()
    {
        var config = WriteCapturesState();
        Quiet(() => CapturesCommand.Create().Invoke(new[] { "suppress", CapturedKey[..8], "-c", config }));
        _ansiOut.GetStringBuilder().Clear();

        Quiet(() => CapturesCommand.Create().Invoke(new[] { "list", "-c", config }));

        var text = _ansiOut.ToString();
        Assert.DoesNotContain(CapturedKey[..8], text);
        Assert.Contains(OtherKey[..8], text);
    }

    [Fact]
    public void CapturesList_All_LabelsAnEntrySuppressedOnlyInTheSuppressionsMap()
    {
        var config = WriteCapturesState();
        Quiet(() => CapturesCommand.Create().Invoke(new[] { "suppress", CapturedKey[..8], "-c", config }));
        _ansiOut.GetStringBuilder().Clear();

        Quiet(() => CapturesCommand.Create().Invoke(new[] { "list", "--all", "-c", config }));

        Assert.Contains("yes", RowFor(CapturedKey));
        Assert.DoesNotContain("yes", RowFor(OtherKey));
    }

    [Fact]
    public void CapturesUnsuppress_ShowsTheEntryAgainAfterAMonitorRunCopiedTheFlag()
    {
        // A monitor run copies the suppression onto captured_errors[key].suppressed, so the
        // state holds both. Unsuppress has to clear both, as the web UI does.
        var config = WriteCapturesState(flagFirstSuppressed: true, mapFirstSuppressed: true);

        Quiet(() => CapturesCommand.Create().Invoke(new[] { "unsuppress", CapturedKey[..8], "-c", config }));
        Assert.Equal(0, Environment.ExitCode);
        _ansiOut.GetStringBuilder().Clear();

        Quiet(() => CapturesCommand.Create().Invoke(new[] { "list", "-c", config }));

        Assert.Contains(CapturedKey[..8], _ansiOut.ToString());
    }

    [Fact]
    public void CapturesUnsuppress_ClearsAnEntryFlaggedSuppressedWithNoMapEntry()
    {
        var config = WriteCapturesState(flagFirstSuppressed: true);

        Quiet(() => CapturesCommand.Create().Invoke(new[] { "unsuppress", CapturedKey[..8], "-c", config }));
        Assert.Equal(0, Environment.ExitCode);
        _ansiOut.GetStringBuilder().Clear();

        Quiet(() => CapturesCommand.Create().Invoke(new[] { "list", "-c", config }));

        Assert.Contains(CapturedKey[..8], _ansiOut.ToString());
    }

    [Fact]
    public void CapturesList_All_IncludesEntriesFlaggedSuppressed()
    {
        var config = WriteCapturesState(flagFirstSuppressed: true);

        Quiet(() => CapturesCommand.Create().Invoke(new[] { "list", "-c", config }));
        Assert.DoesNotContain(CapturedKey[..8], _ansiOut.ToString());

        _ansiOut.GetStringBuilder().Clear();
        Quiet(() => CapturesCommand.Create().Invoke(new[] { "list", "--all", "-c", config }));
        Assert.Contains(CapturedKey[..8], _ansiOut.ToString());
    }

    // ---- helpers ----

    private string WriteServerConfig(string password)
    {
        var path = Path.Combine(_dir, "veeam-vhc-aws.yaml");
        File.WriteAllText(path,
            "servers:\n" +
            "  - name: lab\n" +
            "    type: vbr\n" +
            "    url: https://vbr:9419\n" +
            "    username: admin\n" +
            $"    password: {password}\n" +
            "    verify_ssl: true\n");
        return path;
    }

    private string StatePath => Path.Combine(_dir, "veeam-vhc-aws-state.json");

    private string WriteCapturesState(bool flagFirstSuppressed = false, bool mapFirstSuppressed = false)
    {
        var config = Path.Combine(_dir, "veeam-vhc-aws.yaml");
        File.WriteAllText(config, "servers: []\n");
        var flag = flagFirstSuppressed ? "true" : "false";
        var suppressions = mapFirstSuppressed
            ? $$"""{ "{{CapturedKey}}": { "suppressed_at": "2026-10-15T10:05:00Z" } }"""
            : "{}";
        File.WriteAllText(StatePath, $$"""
            {
              "findings": {},
              "captured_errors": {
                "{{CapturedKey}}": { "resource": "[s1] repo:a", "text": "Repository is low on space", "severity": "warning", "count": 2, "last_seen": "2026-10-15T10:00:00Z", "suppressed": {{flag}} },
                "{{OtherKey}}": { "resource": "[s1] repo:b", "text": "Repository unreachable", "severity": "critical", "count": 1, "last_seen": "2026-10-15T09:00:00Z", "suppressed": false }
              },
              "suppressions": {{suppressions}}
            }
            """);
        return config;
    }

    private string RowFor(string key) =>
        _ansiOut.ToString().Split('\n').Single(line => line.Contains(key[..8]));

    private JsonElement Suppressions()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(StatePath));
        return doc.RootElement.GetProperty("suppressions").Clone();
    }

    private static int Quiet(Func<int> run)
    {
        var priorOut = Console.Out;
        var priorErr = Console.Error;
        try
        {
            Console.SetOut(new StringWriter());
            Console.SetError(new StringWriter());
            return run();
        }
        finally
        {
            Console.SetOut(priorOut);
            Console.SetError(priorErr);
        }
    }
}
