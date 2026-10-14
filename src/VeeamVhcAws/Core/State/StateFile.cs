using System.Collections.Concurrent;

namespace VeeamVhcAws.Core.State;

public static class StateFile
{
    private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.Ordinal);

    public static object Gate(string path) =>
        Gates.GetOrAdd(Path.GetFullPath(path), static _ => new object());

    public static FileStream Acquire(string path)
    {
        var full = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        return new FileStream(full + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    public static void WriteAtomic(string path, string contents)
    {
        var full = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var tmp = full + ".tmp." + Guid.NewGuid().ToString("n");
        File.WriteAllText(tmp, contents);
        File.Move(tmp, full, overwrite: true);
    }
}
