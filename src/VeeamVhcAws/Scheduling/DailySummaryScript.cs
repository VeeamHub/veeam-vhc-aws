using System.Reflection;

namespace VeeamVhcAws.Scheduling;

internal static class DailySummaryScript
{
    internal static string Locate()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "scripts", "vhc-tasks.ps1");
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }

        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("VeeamVhcAws.scripts.vhc-tasks.ps1")
            ?? throw new InvalidOperationException("The daily summary task script is not installed with this build.");
        using var reader = new StreamReader(stream);
        var dest = Path.Combine(Path.GetTempPath(), "vhc-tasks.ps1");
        File.WriteAllText(dest, reader.ReadToEnd());
        return dest;
    }
}
