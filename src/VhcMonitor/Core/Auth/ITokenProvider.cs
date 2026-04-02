namespace VhcMonitor.Core.Auth;

public interface ITokenProvider
{
    Dictionary<string, string> GetHeaders();
}
