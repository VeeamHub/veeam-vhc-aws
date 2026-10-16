using System.Diagnostics;
using Xunit;

namespace VeeamVhcAws.Tests.Installer;

/// <summary>
/// Off-Windows checks for the admin UI shortcut logic in setup.ps1. The pwsh-backed tests
/// return early when PowerShell 7 is not installed (xunit 2.x has no dynamic skip); the
/// switch/text assertions always run. None of this exercises WScript.Shell or a real .lnk.
/// </summary>
public class SetupShortcutTests
{
    private static string SetupPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "setup.ps1")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "setup.ps1");
    }

    private static string? FindPwsh()
    {
        var names = OperatingSystem.IsWindows() ? new[] { "pwsh.exe" } : new[] { "pwsh" };
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Concat(new[] { "/opt/homebrew/bin", "/usr/local/bin", "/usr/bin" });
        foreach (var d in dirs)
            foreach (var n in names)
            {
                var candidate = Path.Combine(d, n);
                if (File.Exists(candidate)) return candidate;
            }
        return null;
    }

    private static (int ExitCode, string Output) RunPwsh(string pwsh, string script)
    {
        var psi = new ProcessStartInfo(pwsh)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(script);
        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEndAsync();
        var stderr = proc.StandardError.ReadToEndAsync();
        Assert.True(proc.WaitForExit(60_000), "pwsh did not exit within 60s");
        return (proc.ExitCode, stdout.Result + stderr.Result);
    }

    // Loads only the named top-level functions from setup.ps1 (via the PowerShell AST),
    // so the installer itself is never executed.
    private static string LoadFunctions(params string[] names)
    {
        var setup = SetupPath().Replace("'", "''");
        var list = string.Join(",", names.Select(n => $"'{n}'"));
        return
            "$ErrorActionPreference='Stop';" +
            $"$ast=[System.Management.Automation.Language.Parser]::ParseFile('{setup}',[ref]$null,[ref]$null);" +
            $"foreach($n in @({list})){{" +
            "$f=$ast.FindAll({param($a) $a -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $a.Name -eq $n},$true)|Select-Object -First 1;" +
            "if(-not $f){throw \"function $n not found\"};" +
            ". ([scriptblock]::Create($f.Extent.Text))};";
    }

    [Fact]
    public void Setup_ParsesWithoutErrors()
    {
        var pwsh = FindPwsh();
        if (pwsh is null) return;
        var setup = SetupPath().Replace("'", "''");
        var (code, output) = RunPwsh(pwsh,
            $"$e=$null;[void][System.Management.Automation.Language.Parser]::ParseFile('{setup}',[ref]$null,[ref]$e);" +
            "if($e.Count -gt 0){$e|ForEach-Object{$_.ToString()};exit 1}; 'PARSE_OK'");
        Assert.True(code == 0, output);
        Assert.Contains("PARSE_OK", output);
    }

    [Fact]
    public void Setup_DeclaresShortcutSwitches_AndUninstallRemovesBothShortcuts()
    {
        var text = File.ReadAllText(SetupPath());
        Assert.Contains("[switch]$NoShortcut", text);
        Assert.Contains("[switch]$DesktopShortcut", text);
        Assert.Contains("Microsoft\\Windows\\Start Menu\\Programs\\VHC AWS Admin.lnk", text);
        Assert.Contains("Join-Path $env:PUBLIC \"Desktop\\VHC AWS Admin.lnk\"", text);
        Assert.Contains("@($shortcutPaths.StartMenu, $shortcutPaths.Desktop)", text);
        Assert.Contains("Start Menu -> VHC AWS Admin", text);
        Assert.Contains("WScript.Shell", text);
        Assert.Contains("$link.WindowStyle = 1", text);
    }

    [Fact]
    public void Setup_RunAsAdministratorByte_SetsOnlyBit0x20AtOffset0x15()
    {
        var pwsh = FindPwsh();
        if (pwsh is null) return;
        var (code, output) = RunPwsh(pwsh, LoadFunctions("Set-LnkRunAsAdministrator") +
            "$in=[byte[]]::new(0x4C); $in[0x15]=0x09; $in[0]=0x4C;" +
            "$out=Set-LnkRunAsAdministrator -Bytes $in;" +
            "\"len=$($out.Length) b15=$($out[0x15]) b0=$($out[0]) orig15=$($in[0x15])\";" +
            "try{[void](Set-LnkRunAsAdministrator -Bytes ([byte[]]::new(4)))}catch{'SHORT_THROWS'}");
        Assert.True(code == 0, output);
        Assert.Contains("len=76 b15=41 b0=76 orig15=9", output);
        Assert.Contains("SHORT_THROWS", output);
    }

    [Fact]
    public void Setup_ShortcutArguments_RunTheUiOnTheDefaultSecureBind()
    {
        var pwsh = FindPwsh();
        if (pwsh is null) return;
        var (code, output) = RunPwsh(pwsh, LoadFunctions("Get-VhcShortcutArguments") +
            "Get-VhcShortcutArguments -ConfigPath 'C:\\Program Files\\VHC\\veeam-vhc-aws.yaml'");
        Assert.True(code == 0, output);
        var args = output.Trim();
        Assert.Equal("ui -c \"C:\\Program Files\\VHC\\veeam-vhc-aws.yaml\"", args);
        Assert.Contains("ui -c", args);
        Assert.DoesNotContain("--bind", args);
        Assert.DoesNotContain("--port", args);
        Assert.DoesNotContain("token", args, StringComparison.OrdinalIgnoreCase);
    }
}
