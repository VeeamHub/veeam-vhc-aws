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
}
