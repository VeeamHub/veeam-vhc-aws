using System.Text.Json;
using Xunit;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using VeeamVhcAws.Core.Auth;
using VeeamVhcAws.Core.Clients;

namespace VeeamVhcAws.Tests.Integration;

public class VbrBackupsPaginationTests : IDisposable
{
    private readonly WireMockServer _server;

    public VbrBackupsPaginationTests()
    {
        _server = WireMockServer.Start();
        _server.Given(Request.Create().WithPath("/api/oauth2/token").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(JsonSerializer.Serialize(new
                {
                    access_token = "test-token",
                    token_type = "Bearer",
                    expires_in = 3600
                })));
    }

    public void Dispose()
    {
        _server.Stop();
        _server.Dispose();
    }

    private VbrClient Client(int pageSize, int maxPages) =>
        new(_server.Url!, new VbrAuth(_server.Url!, "admin", "password", verifySsl: false),
            verifySsl: false, timeout: 2, retryCount: 0, retryDelay: 0,
            pageSize: pageSize, maxPages: maxPages);

    [Fact(Timeout = 5000)]
    public async Task GetBackups_StopsWhenFallbackPageFetchesNothing()
    {
        _server.Given(Request.Create().WithPath("/api/v1/backups").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(500).WithBody("unavailable"));

        var backups = await Task.Run(() => Client(pageSize: 2, maxPages: 2).GetBackups());

        Assert.Empty(backups);
    }

    [Fact(Timeout = 5000)]
    public async Task GetBackups_FallbackStopsAtMaxPages()
    {
        _server.Given(Request.Create().WithPath("/api/v1/backups").WithParam("limit", "2").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(500).WithBody("batch failed"));
        _server.Given(Request.Create().WithPath("/api/v1/backups").WithParam("limit", "1").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{\"data\":[{\"id\":\"b\"}]}"));

        var backups = await Task.Run(() => Client(pageSize: 2, maxPages: 2).GetBackups());

        Assert.Equal(4, backups.Count);
    }
}
