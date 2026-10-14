using System.CommandLine;
using VeeamVhcAws.Commands;
using Xunit;

namespace VeeamVhcAws.Tests.Commands;

[Collection("console")]
public class UiRequireTokenTests
{
    [Fact]
    public void Loopback_StaysOpenUnlessRequireTokenIsSet()
    {
        var open = UiCommand.BuildAuthOptions(isLoopback: true, requireToken: false, tokenPath: "unused");
        Assert.False(open.RequireToken);
        Assert.Equal("", open.Token);

        var path = Path.Combine(Path.GetTempPath(), "vhc-token-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            var forced = UiCommand.BuildAuthOptions(isLoopback: true, requireToken: true, tokenPath: path);
            Assert.True(forced.RequireToken);
            Assert.Equal(64, forced.Token.Length);
            Assert.Equal(forced.Token, File.ReadAllText(path).Trim());

            var remote = UiCommand.BuildAuthOptions(isLoopback: false, requireToken: false, tokenPath: path);
            Assert.True(remote.RequireToken);
            Assert.Equal(forced.Token, remote.Token);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Help_DocumentsRequireToken()
    {
        var help = new StringWriter();
        var previous = Console.Out;
        try
        {
            Console.SetOut(help);
            Assert.Equal(0, UiCommand.Create().Invoke("--help"));
        }
        finally
        {
            Console.SetOut(previous);
        }

        var text = help.ToString();
        Assert.Contains("--require-token", text);
        Assert.Contains("loopback", text);
    }
}
