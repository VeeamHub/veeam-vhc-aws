using System.Diagnostics;
using Xunit;

namespace VeeamVhcAws.Tests.Security;

public class CredentialGateTests
{
    private static string ScriptPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, ".github", "workflows", "security.yml")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, ".github", "scripts", "check-plaintext-credentials.sh");
    }

    [Fact]
    public void Gate_RejectsLiteralSmtpPassword()
    {
        var script = ScriptPath();
        Assert.True(File.Exists(script));
        var dir = Directory.CreateTempSubdirectory("vhc-gate-");
        var file = Path.Combine(dir.FullName, "mail.yaml");
        File.WriteAllText(file, "smtp_password: hunter2\n");
        try
        {
            Assert.NotEqual(0, Run(script, file).ExitCode);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Gate_AllowsEncPassword()
    {
        var script = ScriptPath();
        Assert.True(File.Exists(script));
        var dir = Directory.CreateTempSubdirectory("vhc-gate-");
        var file = Path.Combine(dir.FullName, "mail.yaml");
        File.WriteAllText(file, "password: \"ENC:abc123\"\nsmtp_password: \"ENC:def456\"\n");
        try
        {
            var result = Run(script, file);
            Assert.Equal(0, result.ExitCode);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    private static (int ExitCode, string Output) Run(string script, string file)
    {
        using var proc = Process.Start(new ProcessStartInfo
        {
            FileName = "bash",
            ArgumentList = { script, file },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        });
        Assert.NotNull(proc);
        var output = proc!.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        return (proc.ExitCode, output);
    }
}
