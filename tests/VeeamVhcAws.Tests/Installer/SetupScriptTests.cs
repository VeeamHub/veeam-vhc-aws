using Xunit;

namespace VeeamVhcAws.Tests.Installer;

public class SetupScriptTests
{
    private static string SetupText()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "setup.ps1")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, "setup.ps1"));
    }

    [Fact]
    public void DataDirectory_IsNotWritableByUsers()
    {
        var text = SetupText();
        Assert.DoesNotContain("Users:(OI)(CI)M", text);
        Assert.Contains("function Protect-VhcSystemDirectory", text);
        Assert.Contains("function Set-LoggingPathToVhcLogs", text);
        Assert.Contains("NT AUTHORITY\\SYSTEM:(OI)(CI)F", text);
        Assert.Contains("BUILTIN\\Administrators:(OI)(CI)F", text);
    }

    [Fact]
    public void ServerBlocks_DefaultVerifySslTrue_AndWarnWhenDisabled()
    {
        var text = SetupText();
        Assert.DoesNotContain("verify_ssl: false", text);
        Assert.Contains("verify_ssl: $verifySslYaml", text);
        Assert.Contains("TLS verification is off", text);
    }

    [Fact]
    public void Interval_DefaultsToFiveMinutes_AndHonorsTheParameter()
    {
        var text = SetupText();
        Assert.Contains("[int]$IntervalMinutes = 5", text);
        Assert.Contains("default: 5", text);
        Assert.Contains("PSBoundParameters.ContainsKey('IntervalMinutes')", text);
    }

    [Fact]
    public void GeneratedConfig_EmitsDataDirectory_AndKeepsStateAndLogUnderIt()
    {
        var text = SetupText();
        Assert.Contains("$dataDir = Join-Path $env:ProgramData \"VHC\"", text);
        Assert.Contains("$dataDirYaml = $dataDir -replace '\\\\', '/'", text);
        Assert.Contains("data_directory: '$dataDirYaml'", text);
        Assert.Contains("$statePath = Join-Path $dataDir \"veeam-vhc-aws-state.json\"", text);
        Assert.Contains("$logDir = Join-Path $dataDir \"logs\"", text);
        Assert.Contains("$logPath = Join-Path $logDir \"veeam-vhc-aws.log\"", text);
        Assert.Contains("state_file: '$stateYaml'", text);
        Assert.Contains("file: '$logYaml'", text);
    }

    [Fact]
    public void LogPath_IsSingleQuotedWithForwardSlashes()
    {
        var text = SetupText();
        Assert.Contains("$logYaml = $logPath -replace '\\\\', '/'", text);
        Assert.Contains("file: '$logYaml'", text);
        Assert.DoesNotContain("file: \"$($logPath", text);
    }

    [Fact]
    public void DailySummaryTask_UsesTheFailureAlertWrapper()
    {
        var text = SetupText();
        Assert.Contains("-Action register-daily", text);
        Assert.DoesNotContain("-Argument \"summary --config", text);

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "scripts", "vhc-tasks.ps1")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        var script = File.ReadAllText(Path.Combine(dir!.FullName, "scripts", "vhc-tasks.ps1"));
        Assert.Contains("Veeam VHC AWS Daily Summary", script);
        Assert.Contains("-Command $Cmd", script);
        Assert.Contains("-Cmd 'summary'", script);
        Assert.Contains("if ($code -ne 0)", script);
        Assert.Contains("Invoke-WebRequest", script);
        Assert.Contains("exit $code", script);
    }
}
