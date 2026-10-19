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

        if (IsFrameworkAsset(context.Request.Method, path))
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

    internal static bool IsFrameworkAsset(string method, string path)
    {
        if (!HttpMethods.IsGet(method) && !HttpMethods.IsHead(method))
            return false;
        if (!path.StartsWith("/_framework/", StringComparison.OrdinalIgnoreCase))
            return false;
        var rest = path["/_framework/".Length..];
        if (rest.Length == 0 || rest.Contains('/') || rest.Contains('\\') || rest.Contains("..", StringComparison.Ordinal))
            return false;
        var ext = Path.GetExtension(rest);
        return ext.Equals(".js", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".css", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".wasm", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".json", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".map", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".dat", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".woff", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".woff2", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".gz", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".br", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".mjs", StringComparison.OrdinalIgnoreCase);
    }

    internal static byte[] TokenDigest(string value) =>
        SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));

    internal static bool TokensEqual(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(TokenDigest(a), TokenDigest(b));

    public static string GenerateToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
