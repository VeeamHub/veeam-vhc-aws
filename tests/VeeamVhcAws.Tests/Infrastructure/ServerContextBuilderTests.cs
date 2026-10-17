using System.Reflection;
using Xunit;
using VeeamVhcAws.Core.Clients;
using VeeamVhcAws.Infrastructure;

namespace VeeamVhcAws.Tests.Infrastructure;

public class ServerContextBuilderTests
{
    [Fact]
    public void BuildServerContext_DefaultsSessionTimeoutTo600LikeTheLoaderAndSettingsPage()
    {
        var server = new Dictionary<string, object>
        {
            ["name"] = "lab",
            ["type"] = "vbr",
            ["url"] = "https://vbr.example:9419",
            ["username"] = "admin",
            ["password"] = "pw",
        };

        var ctx = ServerContextBuilder.BuildServerContext(server, new Dictionary<string, object>());

        var client = Assert.IsType<VbrClient>(ctx.VbrClient);
        var sessionTimeout = (int)typeof(VbrClient)
            .GetField("_sessionTimeout", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(client)!;
        Assert.Equal(600, sessionTimeout);
    }
}
