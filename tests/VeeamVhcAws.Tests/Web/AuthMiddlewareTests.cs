using Microsoft.AspNetCore.Http;
using VeeamVhcAws.Commands;
using VeeamVhcAws.Web;
using Xunit;

namespace VeeamVhcAws.Tests.Web;

/// <summary>
/// The admin UI picks its auth options from the bind address (UiCommand.BuildAuthOptions)
/// and AuthMiddleware enforces them. Loopback without --require-token stays open; any
/// non-loopback bind demands the token on every path except /healthz and Blazor assets.
/// </summary>
public class AuthMiddlewareTests : IDisposable
{
    private readonly string _tokenPath =
        Path.Combine(Path.GetTempPath(), "vhc-auth-mw-" + Guid.NewGuid().ToString("N") + ".txt");

    public void Dispose()
    {
        if (File.Exists(_tokenPath))
            File.Delete(_tokenPath);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/api/config")]
    [InlineData("/api/state")]
    [InlineData("/servers")]
    public async Task Loopback_WithoutRequireToken_LetsEveryRequestThrough(string path)
    {
        var options = UiCommand.BuildAuthOptions(isLoopback: true, requireToken: false, tokenPath: _tokenPath);

        var ctx = await Send(options, path);

        Assert.Equal(204, ctx.Response.StatusCode);
        Assert.False(File.Exists(_tokenPath));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/api/config")]
    [InlineData("/api/state")]
    public async Task NonLoopback_WithoutToken_IsUnauthorized(string path)
    {
        var options = UiCommand.BuildAuthOptions(isLoopback: false, requireToken: false, tokenPath: _tokenPath);

        var ctx = await Send(options, path);

        Assert.Equal(401, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task NonLoopback_Healthz_NeedsNoToken()
    {
        var options = UiCommand.BuildAuthOptions(isLoopback: false, requireToken: false, tokenPath: _tokenPath);

        Assert.Equal(204, (await Send(options, "/healthz")).Response.StatusCode);
        Assert.Equal(204, (await Send(options, "/HEALTHZ")).Response.StatusCode);
    }

    [Fact]
    public async Task NonLoopback_WrongToken_IsUnauthorized_AndSetsNoCookie()
    {
        var options = UiCommand.BuildAuthOptions(isLoopback: false, requireToken: false, tokenPath: _tokenPath);

        var ctx = await Send(options, "/api/config", query: "?t=" + new string('0', 64));

        Assert.Equal(401, ctx.Response.StatusCode);
        Assert.Empty(ctx.Response.Headers.SetCookie.ToString());
    }

    [Fact]
    public async Task NonLoopback_QueryToken_PassesAndSetsAStrictHttpOnlyCookie()
    {
        var options = UiCommand.BuildAuthOptions(isLoopback: false, requireToken: false, tokenPath: _tokenPath);

        var ctx = await Send(options, "/api/config", query: "?t=" + options.Token);

        Assert.Equal(204, ctx.Response.StatusCode);
        var cookie = ctx.Response.Headers.SetCookie.ToString();
        Assert.StartsWith("vhc_auth=" + options.Token, cookie, StringComparison.Ordinal);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        // Plain HTTP: the cookie is not marked Secure.
        Assert.DoesNotContain("secure", cookie.Replace("samesite", "", StringComparison.OrdinalIgnoreCase),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NonLoopback_QueryTokenOverHttps_SetsASecureCookie()
    {
        var options = UiCommand.BuildAuthOptions(isLoopback: false, requireToken: false, tokenPath: _tokenPath);

        var ctx = await Send(options, "/", query: "?t=" + options.Token, https: true);

        Assert.Equal(204, ctx.Response.StatusCode);
        Assert.Contains("; secure", ctx.Response.Headers.SetCookie.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NonLoopback_CookieToken_Passes()
    {
        var options = UiCommand.BuildAuthOptions(isLoopback: false, requireToken: false, tokenPath: _tokenPath);

        var ctx = await Send(options, "/api/state", cookie: "vhc_auth=" + options.Token);

        Assert.Equal(204, ctx.Response.StatusCode);
    }

    private static async Task<DefaultHttpContext> Send(
        AuthOptions options, string path, string query = "", string? cookie = null, bool https = false)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "GET";
        ctx.Request.Scheme = https ? "https" : "http";
        ctx.Request.Path = path;
        ctx.Request.QueryString = new QueryString(query);
        if (cookie != null)
            ctx.Request.Headers.Cookie = cookie;
        ctx.Response.Body = new MemoryStream();

        var middleware = new AuthMiddleware(
            inner =>
            {
                inner.Response.StatusCode = 204;
                return Task.CompletedTask;
            },
            options);
        await middleware.InvokeAsync(ctx);
        return ctx;
    }
}
