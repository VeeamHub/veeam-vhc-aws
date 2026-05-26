using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Serilog;
using SerilogILogger = Serilog.ILogger;

namespace VeeamVhcAws.Web;

public class AuthOptions
{
    public bool RequireToken { get; init; }
    public string Token { get; init; } = "";
}

public class AuthMiddleware
{
    private static readonly SerilogILogger Logger = Log.ForContext<AuthMiddleware>();
    private const string CookieName = "vhc_auth";
    private readonly RequestDelegate _next;
    private readonly AuthOptions _options;

    public AuthMiddleware(RequestDelegate next, AuthOptions options)
    {
        _next = next;
        _options = options;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!_options.RequireToken)
        {
            await _next(context);
            return;
        }

        var path = context.Request.Path.Value ?? "";
        if (path.Equals("/healthz", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        // Allow Blazor static framework assets (JS/WASM) without auth.
        // /_blazor (SignalR hub) is NOT bypassed — cookie auth is already set by the
        // time the circuit connects, so the hub requests pass through IsAuthenticated normally.
        if (path.StartsWith("/_framework", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        if (IsAuthenticated(context))
        {
            await _next(context);
            return;
        }

        Logger.Warning("Unauthenticated request to {Path} from {Ip}", path, context.Connection.RemoteIpAddress);
        context.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
        await context.Response.WriteAsync(
            "Unauthorized.\n\n" +
            "This UI is bound to a non-loopback address and requires the auth token.\n" +
            "Append ?t=<token> to the URL or set the 'vhc_auth' cookie.\n" +
            "The token is shown in the server console when 'veeam-vhc-aws ui' starts.\n");
    }

    private bool IsAuthenticated(HttpContext context)
    {
        var queryToken = context.Request.Query["t"].ToString();
        if (!string.IsNullOrEmpty(queryToken) && TokensEqual(queryToken, _options.Token))
        {
            context.Response.Cookies.Append(CookieName, _options.Token, new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Strict,
                Secure = context.Request.IsHttps,
                MaxAge = TimeSpan.FromDays(30),
            });
            return true;
        }

        var cookieToken = context.Request.Cookies[CookieName];
        return !string.IsNullOrEmpty(cookieToken) && TokensEqual(cookieToken, _options.Token);
    }

    private static bool TokensEqual(string a, string b)
    {
        if (a.Length != b.Length) return false;
        var aBytes = System.Text.Encoding.UTF8.GetBytes(a);
        var bBytes = System.Text.Encoding.UTF8.GetBytes(b);
        return CryptographicOperations.FixedTimeEquals(aBytes, bBytes);
    }

    public static string GenerateToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
