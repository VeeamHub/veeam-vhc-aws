using System.Net.Http.Headers;
using System.Text.Json;
using Serilog;

namespace VhcMonitor.Core.Auth;

public class VbrAuth : ITokenProvider
{
    private static readonly ILogger Logger = Log.ForContext<VbrAuth>();
    private readonly string _baseUrl;
    private readonly string _username;
    private readonly string _password;
    private readonly string _apiVersion;
    private readonly bool _verifySsl;
    private string? _token;
    private DateTimeOffset _tokenExpiry = DateTimeOffset.MinValue;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public VbrAuth(string baseUrl, string username, string password,
        string apiVersion = "1.3-rev1", bool verifySsl = true)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _username = username;
        _password = password;
        _apiVersion = apiVersion;
        _verifySsl = verifySsl;
    }

    private bool IsTokenExpired() =>
        _token == null || DateTimeOffset.UtcNow >= _tokenExpiry.AddSeconds(-60);

    public string GetToken()
    {
        _semaphore.Wait();
        try
        {
            if (!IsTokenExpired() && _token != null)
            {
                Logger.Debug("VBR token still valid, reusing cached token");
                return _token;
            }

            var url = $"{_baseUrl}/api/oauth2/token";
            Logger.Information("VBR OAuth2 token request to {Url} for user {User}", url, _username);

            var handler = new HttpClientHandler();
            if (!_verifySsl)
                handler.ServerCertificateCustomValidationCallback =
                    HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;

            using var client = new HttpClient(handler);
            client.DefaultRequestHeaders.TryAddWithoutValidation("x-api-version", _apiVersion);

            var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "password",
                ["username"] = _username,
                ["password"] = _password,
            });

            var response = client.PostAsync(url, content).GetAwaiter().GetResult();
            response.EnsureSuccessStatusCode();

            var json = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            var tokenData = JsonDocument.Parse(json).RootElement;

            _token = tokenData.GetProperty("access_token").GetString()!;
            var expiresIn = tokenData.TryGetProperty("expires_in", out var exp) ? exp.GetInt32() : 900;
            _tokenExpiry = DateTimeOffset.UtcNow.AddSeconds(expiresIn);

            Logger.Information("VBR OAuth2 token obtained, expires in {ExpiresIn}s", expiresIn);
            return _token;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public Dictionary<string, string> GetHeaders()
    {
        var token = GetToken();
        return new Dictionary<string, string>
        {
            ["Authorization"] = $"Bearer {token}",
            ["x-api-version"] = _apiVersion,
            ["Accept"] = "application/json",
        };
    }
}
