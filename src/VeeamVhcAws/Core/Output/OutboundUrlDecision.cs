using System.Net;
using System.Net.Sockets;

namespace VeeamVhcAws.Core.Output;

public readonly record struct OutboundUrlDecision(bool Allowed, string Host, string Reason)
{
    public static OutboundUrlDecision Decide(string rawUrl, bool allowLoopback)
    {
        if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri))
            return new(false, "invalid", "scheme");

        var host = string.IsNullOrEmpty(uri.Host) ? "invalid" : uri.Host;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return new(false, host, "scheme");
        if (host == "invalid")
            return new(false, host, "scheme");

        if (IPAddress.TryParse(host, out var literal))
            return DecideAddress(literal, host, allowLoopback);

        IPAddress[] addresses;
        try
        {
            addresses = Dns.GetHostAddresses(host);
        }
        catch (Exception)
        {
            return new(false, host, "unresolved");
        }

        if (addresses.Length == 0)
            return new(false, host, "unresolved");

        foreach (var address in addresses)
        {
            var decision = DecideAddress(address, host, allowLoopback);
            if (!decision.Allowed)
                return decision;
        }

        return new(true, host, "");
    }

    public static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler { AllowAutoRedirect = false };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
    }

    private static OutboundUrlDecision DecideAddress(IPAddress ip, string host, bool allowLoopback)
    {
        if (ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = ip.GetAddressBytes();
            if (bytes[0] == 169 && bytes[1] == 254 && bytes[2] == 169 && bytes[3] == 254)
                return new(false, host, "metadata");
            if (bytes[0] == 169 && bytes[1] == 254)
                return new(false, host, "link-local");
            if (bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 0 && bytes[3] == 0)
                return new(false, host, "unspecified");
            if (bytes[0] == 127)
                return allowLoopback ? new(true, host, "") : new(false, host, "loopback");
            return new(true, host, "");
        }

        if (ip.Equals(IPAddress.IPv6Any))
            return new(false, host, "unspecified");
        if (IPAddress.IsLoopback(ip))
            return allowLoopback ? new(true, host, "") : new(false, host, "loopback");
        if (ip.IsIPv6LinkLocal)
            return new(false, host, "link-local");
        return new(true, host, "");
    }
}
