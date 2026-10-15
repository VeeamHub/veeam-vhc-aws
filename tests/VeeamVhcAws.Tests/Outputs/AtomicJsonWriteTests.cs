using System.Text.Json;
using VeeamVhcAws.Core.Models;
using VeeamVhcAws.Core.State;
using VeeamVhcAws.Outputs;
using Xunit;

namespace VeeamVhcAws.Tests.Outputs;

/// <summary>
/// JSON the app persists must never be left half-written. Each write goes to a temp file in
/// the same directory, is flushed, and then replaces the target in one rename.
/// </summary>
public class AtomicJsonWriteTests : IDisposable
{
    private readonly string _dir;

    public AtomicJsonWriteTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "vhc-atomic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private static List<MonitorResult> Results(string message) => new()
    {
        new(MonitorType.RepoHealth, DateTime.UtcNow, 10, Severity.Warning,
            new List<Finding> { new(Severity.Warning, "repo:a", message) }, server: "s1"),
    };

    // An in-place write truncates the live file and then fills it, so a reader (or a crash)
    // between the two sees a partial document. A rename swaps the whole file: a handle opened
    // before the write still reads the complete old document.
    [Fact]
    public void JsonFile_ReplacesTheFile_InsteadOfRewritingItInPlace()
    {
        var path = Path.Combine(_dir, "out.json");
        new JsonFileHandler(path).Emit(Results("first run"));

        using var before = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        new JsonFileHandler(path).Emit(Results("second run"));

        using (var reader = new StreamReader(before))
        {
            var old = reader.ReadToEnd();
            Assert.Contains("first run", old);
            JsonDocument.Parse(old).Dispose();
        }
        Assert.Contains("second run", File.ReadAllText(path));
    }

    [Fact]
    public void JsonFile_LeavesNoTempFiles()
    {
        var path = Path.Combine(_dir, "out.json");
        new JsonFileHandler(path).Emit(Results("one"));
        new JsonFileHandler(path).Emit(Results("two"));

        Assert.Equal(new[] { "out.json" }, Directory.GetFiles(_dir).Select(Path.GetFileName).ToArray());
    }

    [Fact]
    public void WriteAtomic_FailedReplace_RemovesItsTempFile()
    {
        // The target is a directory, so the final rename fails after the temp file was written.
        var target = Path.Combine(_dir, "state.json");
        Directory.CreateDirectory(target);

        Assert.ThrowsAny<Exception>(() => StateFile.WriteAtomic(target, "{}"));

        Assert.Empty(Directory.GetFiles(_dir));
    }
}
