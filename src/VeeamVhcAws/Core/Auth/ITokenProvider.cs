namespace VeeamVhcAws.Core.Auth;

public interface ITokenProvider
{
    Dictionary<string, string> GetHeaders();
}
