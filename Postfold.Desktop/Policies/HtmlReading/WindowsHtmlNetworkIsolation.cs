using System;
using System.Net;
using System.Net.Sockets;

namespace Postfold.Desktop.Policies.HtmlReading;

/// <summary>Reserves an unlistened loopback endpoint to fail native HTTP/WebSocket connections locally.</summary>
internal sealed class WindowsHtmlNetworkIsolation : IDisposable
{
    private readonly Socket _reservation;

    internal WindowsHtmlNetworkIsolation()
    {
        _reservation = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
        {
            ExclusiveAddressUse = true
        };
        _reservation.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)_reservation.LocalEndPoint!).Port;
        // No listener, forwarding, credentials or DIRECT fallback. Include loopback destinations in the proxy rule.
        Arguments = "--proxy-server=socks5://127.0.0.1:" + port + " --proxy-bypass-list=<-loopback> " +
            "--disable-quic --disable-background-networking --dns-prefetch-disable --disable-preconnect";
    }

    internal string Arguments
    {
        get;
    }

    public void Dispose()
    {
        _reservation.Dispose();
    }
}
