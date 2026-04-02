namespace VhcMonitor.Core.Clients;

/// <summary>
/// Minimal Enterprise Manager client — reserved for future implementation.
/// </summary>
public class EmClient
{
    public string BaseUrl { get; }
    public bool VerifySsl { get; }
    public int Timeout { get; }

    public EmClient(string baseUrl = "", bool verifySsl = true, int timeout = 30)
    {
        BaseUrl = baseUrl.TrimEnd('/');
        VerifySsl = verifySsl;
        Timeout = timeout;
    }
}
