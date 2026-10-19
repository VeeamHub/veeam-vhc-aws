using System.Diagnostics;

namespace VeeamVhcAws.Scheduling;

public sealed class WindowsDailySummaryTaskHost : IDailySummaryTaskHost
{
    public bool IsWindows => true;

    public bool Exists()
    {
        var code = Run(new[] { "-Action", "status" }, accept: new[] { 0, 1 });
        return code == 0;
    }

    public void Register(string configPath, string timeOfDay)
    {
        var exe = ResolveExePath();
        Run(new[]
        {
            "-Action", "register-daily",
            "-InstallDir", Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory,
            "-ExePath", exe,
            "-ConfigPath", configPath,
            "-Time", timeOfDay,
        });
    }

    public void Unregister() => Run(new[] { "-Action", "unregister-daily" });

    internal static string ResolveExePath()
    {
        var process = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(process)
            && process.EndsWith("veeam-vhc-aws.exe", StringComparison.OrdinalIgnoreCase))
            return process;

        var beside = Path.Combine(AppContext.BaseDirectory, "veeam-vhc-aws.exe");
        if (File.Exists(beside))
            return beside;
        return process ?? beside;
    }

    private static int Run(string[] actionArgs, int[]? accept = null)
    {
        var script = DailySummaryScript.Locate();
        var psi = new ProcessStartInfo
        {
            FileName = PowershellPath(),
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script })
            psi.ArgumentList.Add(arg);
        foreach (var arg in actionArgs)
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Could not start PowerShell to update the daily summary task.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (accept is null)
        {
            if (process.ExitCode != 0)
                throw new InvalidOperationException(Failure(process.ExitCode, stdout, stderr));
            return process.ExitCode;
        }
        if (!accept.Contains(process.ExitCode))
            throw new InvalidOperationException(Failure(process.ExitCode, stdout, stderr));
        return process.ExitCode;
    }

    private static string Failure(int code, string stdout, string stderr) =>
        $"Daily summary task update failed ({code}): {stderr} {stdout}".Trim();

    private static string PowershellPath()
    {
        var system = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        return File.Exists(system) ? system : "powershell.exe";
    }
}
