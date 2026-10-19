using System.Net.Http.Headers;
using System.Text.Json;
using Serilog;

namespace VeeamVhcAws.Core.Auth;

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
    private HttpClient? _boundClient;

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

    internal void BindClient(HttpClient client) => _boundClient = client;

    internal void Invalidate()
    {
        _semaphore.Wait();
        try
        {
            _token = null;
            _tokenExpiry = DateTimeOffset.MinValue;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    internal void SeedToken(string token)
    {
        _semaphore.Wait();
        try
        {
            _token = token;
            _tokenExpiry = DateTimeOffset.UtcNow.AddHours(1);
        }
        finally
        {
            _semaphore.Release();
        }
    }

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

            var owned = _boundClient == null;
            var client = _boundClient;
            if (client == null)
            {
                var handler = new HttpClientHandler();
                if (!_verifySsl)
                    handler.ServerCertificateCustomValidationCallback =
                        HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
                client = new HttpClient(handler);
            }

            var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "password",
                ["username"] = _username,
                ["password"] = _password,
            });
            var tokenRequest = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
            tokenRequest.Headers.TryAddWithoutValidation("x-api-version", _apiVersion);

            try
            {
                var response = client.SendAsync(tokenRequest).GetAwaiter().GetResult();
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
                if (owned)
                    client.Dispose();
            }
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
