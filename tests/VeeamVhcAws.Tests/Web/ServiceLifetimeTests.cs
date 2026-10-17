using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using VeeamVhcAws.Web;
using VeeamVhcAws.Web.Services;
using Xunit;

namespace VeeamVhcAws.Tests.Web;

public class ServiceLifetimeTests
{
    [Fact]
    public async Task TwoCircuits_ShareOneConfigSaveLock_AndOneStateService()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vhc-lifetime-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var config = Path.Combine(dir, "veeam-vhc-aws.yaml");
        var app = WebHost.Build(
            new WebHostOptions(config, "127.0.0.1", 0),
            new AuthOptions());
        try
        {
            using var left = app.Services.CreateScope();
            using var right = app.Services.CreateScope();
            var configLeft = left.ServiceProvider.GetRequiredService<ConfigService>();
            var configRight = right.ServiceProvider.GetRequiredService<ConfigService>();
            var stateLeft = left.ServiceProvider.GetRequiredService<StateService>();
            var stateRight = right.ServiceProvider.GetRequiredService<StateService>();

            var saveLock = typeof(ConfigService).GetField(
                "_saveLock",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.Same(saveLock!.GetValue(configLeft), saveLock.GetValue(configRight));
            Assert.Same(stateLeft, stateRight);
        }
        finally
        {
            await app.DisposeAsync();
            Directory.Delete(dir, recursive: true);
        }
    }
}
