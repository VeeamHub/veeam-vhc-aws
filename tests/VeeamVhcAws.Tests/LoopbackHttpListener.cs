using System.Net;
using System.Net.Sockets;

namespace VeeamVhcAws.Tests;

// Probing for a free port and binding it later races with other tests that
// do the same, so bind here and retry on a fresh port when it is taken.
internal static class LoopbackHttpListener
{
    public static HttpListener Start(out int port)
    {
        for (var attempt = 0; ; attempt++)
        {
            port = ProbeFreePort();
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
                return listener;
            }
            catch (HttpListenerException) when (attempt < 20)
            {
                listener.Close();
            }
        }
    }

    private static int ProbeFreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
