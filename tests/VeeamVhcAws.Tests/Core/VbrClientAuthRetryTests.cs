using System.Net;
using System.Text;
using VeeamVhcAws.Core.Auth;
using VeeamVhcAws.Core.Clients;
using Xunit;

namespace VeeamVhcAws.Tests.Core;

public class VbrClientAuthRetryTests
{
    [Fact]
    public void Unauthorized_DropsTheToken_AndRetriesOnce()
    {
        var handler = new ScriptedHandler(call => call == 1 ? HttpStatusCode.Unauthorized : HttpStatusCode.OK);
        var client = Build(handler);
        var info = client.GetServerInfo();
        Assert.Equal("lab", info["name"]);
        Assert.Equal(new[] { "cached-token", "fresh" }, handler.BearerTokens);
        Assert.Equal(1, handler.TokenFetches);
    }

    [Fact]
    public void SecondUnauthorized_DoesNotLoop()
    {
        var handler = new ScriptedHandler(_ => HttpStatusCode.Unauthorized);
        var client = Build(handler);
        Assert.Throws<HttpRequestException>(() => client.GetServerInfo());
        Assert.Equal(2, handler.ApiCalls);
    }

    private static VbrClient Build(ScriptedHandler handler)
    {
        var auth = new VbrAuth("http://vbr.test", "user", "pw");
        auth.SeedToken("cached-token");
        return new VbrClient(
            "http://vbr.test",
            auth,
            verifySsl: false,
            timeout: 5,
            retryCount: 5,
            retryDelay: 0,
            handler: handler);
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Func<int, HttpStatusCode> _status;

        public ScriptedHandler(Func<int, HttpStatusCode> status) => _status = status;

        public int ApiCalls { get; private set; }
        public int TokenFetches { get; private set; }
        public List<string> BearerTokens { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            if (path.Contains("oauth2/token", StringComparison.Ordinal))
            {
                TokenFetches++;
                return Task.FromResult(Json(HttpStatusCode.OK, """{"access_token":"fresh","expires_in":900}"""));
            }

            ApiCalls++;
            BearerTokens.Add(request.Headers.Authorization?.Parameter ?? "");
            var status = _status(ApiCalls);
            if (status != HttpStatusCode.OK)
                return Task.FromResult(new HttpResponseMessage(status));
            return Task.FromResult(Json(status, """{"name":"lab"}"""));
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
            new(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
    }
}
