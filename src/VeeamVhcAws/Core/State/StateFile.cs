using System.Collections.Concurrent;
using System.Text;

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

    /// <summary>
    /// Writes to a temp file in the target's directory, flushes it to disk, then renames it over
    /// the target. File.Move with overwrite is a single rename on Linux/macOS and a
    /// MoveFileEx(REPLACE_EXISTING) on Windows, so a reader or a crash sees the old file or the
    /// new one, never a partial one. The temp file is removed if any step fails.
    /// </summary>
    public static void WriteAtomic(string path, string contents)
    {
        var full = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var tmp = full + ".tmp." + Guid.NewGuid().ToString("n");
        try
        {
            using (var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.Write(contents);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            File.Move(tmp, full, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { /* best effort; the original error matters more */ }
            throw;
        }
    }
}
