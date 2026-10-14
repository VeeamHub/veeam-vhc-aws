namespace VeeamVhcAws.Core.Config;

public static class ConfigPathJail
{
    public const string OutsideMessage = "Path is outside the config directory.";

    public static string Resolve(string configFilePath, string candidate)
    {
        var full = Lexical(configFilePath, candidate);
        var realCandidate = RealPath(full);
        var roots = AllowedRoots(configFilePath);
        if (roots.Count == 0)
            throw new InvalidOperationException(OutsideMessage);

        var configRoot = RealPath(roots[0]);
        if (!Path.IsPathRooted(candidate))
        {
            if (!IsInside(configRoot, realCandidate))
                throw new InvalidOperationException(OutsideMessage);
            return full;
        }

        foreach (var root in roots)
        {
            if (IsInside(RealPath(root), realCandidate))
                return full;
        }

        throw new InvalidOperationException(OutsideMessage);
    }

    internal static IReadOnlyList<string> AllowedRoots(string configFilePath)
    {
        var configDir = Path.GetDirectoryName(Path.GetFullPath(configFilePath));
        if (string.IsNullOrEmpty(configDir))
            return Array.Empty<string>();

        var roots = new List<string> { configDir };
        var data = DataDirectory(configFilePath, configDir);
        if (data is not null && !PathsEqual(RealPath(configDir), RealPath(data)))
            roots.Add(data);
        return roots;
    }

    private static string? DataDirectory(string configFilePath, string configDir)
    {
        if (!File.Exists(configFilePath))
            return null;
        var raw = ConfigLoader.LoadConfig(configFilePath).GetSection("global").Get("data_directory", "");
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        return Path.IsPathRooted(raw)
            ? Path.GetFullPath(raw)
            : Path.GetFullPath(Path.Combine(configDir, raw));
    }

    private static bool PathsEqual(string left, string right)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            comparison);
    }

    private static string Lexical(string configPath, string path)
    {
        if (Path.IsPathRooted(path))
            return Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(Path.GetFullPath(configPath));
        if (string.IsNullOrEmpty(dir))
            return Path.GetFullPath(path);
        return Path.GetFullPath(Path.Combine(dir, path));
    }

    private static bool IsInside(string root, string candidate)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        if (string.Equals(root, candidate, comparison))
            return true;
        return candidate.StartsWith(root + Path.DirectorySeparatorChar, comparison);
    }

    private static string RealPath(string path)
    {
        var full = Path.GetFullPath(path);
        var missing = new Stack<string>();
        var current = full;
        while (!Exists(current))
        {
            var name = Path.GetFileName(current);
            if (string.IsNullOrEmpty(name))
                break;
            missing.Push(name);
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || parent == current)
                break;
            current = parent;
        }

        current = ResolveExisting(current);
        while (missing.Count > 0)
            current = Path.Combine(current, missing.Pop());
        return Path.GetFullPath(current);
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    private static string ResolveExisting(string path)
    {
        var seen = new HashSet<string>(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal);
        var current = Path.GetFullPath(path);
        while (seen.Add(current))
        {
            string? link = null;
            if (Directory.Exists(current))
                link = new DirectoryInfo(current).LinkTarget;
            else if (File.Exists(current))
                link = new FileInfo(current).LinkTarget;
            if (string.IsNullOrEmpty(link))
                return current;
            var next = Path.IsPathRooted(link)
                ? link
                : Path.Combine(Path.GetDirectoryName(current)!, link);
            current = Path.GetFullPath(next);
        }

        throw new InvalidOperationException(OutsideMessage);
    }
}
