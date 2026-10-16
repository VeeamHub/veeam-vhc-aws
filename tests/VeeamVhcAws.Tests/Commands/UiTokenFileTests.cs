using System.Reflection;
using VeeamVhcAws.Commands;
using Xunit;

namespace VeeamVhcAws.Tests.Commands;

public class UiTokenFileTests
{
    [Fact]
    public void WriteToken_CreatesOwnerOnlyFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vhc-token-{Guid.NewGuid():N}.txt");
        var token = new string('a', 64);
        try
        {
            var method = typeof(UiCommand).GetMethod("WriteToken", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);
            method!.Invoke(null, new object[] { path, token });

            Assert.Equal(token, File.ReadAllText(path));
            if (!OperatingSystem.IsWindows())
            {
                var mode = File.GetUnixFileMode(path);
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
            }
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
