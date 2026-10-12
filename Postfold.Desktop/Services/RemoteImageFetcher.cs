using Postfold.Core.Enums;
using Postfold.Core.Interfaces;
using Postfold.Core.Models;
using Postfold.Desktop.Interfaces;
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Desktop.Services;

/// <summary>Consent-gated PNG fetcher. Never provides URLs to a browser. Native integration remains disabled.</summary>
public sealed class RemoteImageFetcher : IRemoteImageFetcher
{
    private readonly IDnsResolver _dns;
    private readonly HttpMessageHandler? _fixtureTransport;

    public RemoteImageFetcher()
    {
        _dns = new SystemDnsResolver();
    }

    internal RemoteImageFetcher(HttpMessageHandler fixtureTransport)
    {
        _dns = new SystemDnsResolver();
        _fixtureTransport = fixtureTransport;
    }

    internal RemoteImageFetcher(IDnsResolver dns)
    {
        _dns = dns;
    }

    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        byte[] bytes = address.GetAddressBytes();
        if (bytes.Length != 4)
        {
            // Ordinary global unicast only; special-purpose, documentation and transition ranges fail closed.
            return address.ScopeId == 0 && (bytes[0] & 0xE0) == 0x20 &&
                !(bytes[0] == 0x20 && bytes[1] == 0x01 && (bytes[2] & 0xFE) == 0) &&
                !(bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0D && bytes[3] == 0xB8) &&
                !(bytes[0] == 0x20 && bytes[1] == 0x02) &&
                !(bytes[0] == 0x3F && bytes[1] == 0xFF && (bytes[2] & 0xF0) == 0);
        }

        return bytes[0] is > 0 and < 224 && bytes[0] is not 10 and not 127 &&
            !(bytes[0] == 100 && bytes[1] is >= 64 and <= 127) &&
            !(bytes[0] == 169 && bytes[1] == 254) && !(bytes[0] == 172 && bytes[1] is >= 16 and <= 31) &&
            !(bytes[0] == 192 && (bytes[1] == 168 || bytes[1] == 0 || bytes[1] == 88 && bytes[2] == 99)) &&
            !(bytes[0] == 198 && (bytes[1] is 18 or 19 || bytes[1] == 51 && bytes[2] == 100)) &&
            !(bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113);
    }

    public static bool ValidUrl(Uri url)
    {
        return url.IsAbsoluteUri && url.Scheme == Uri.UriSchemeHttps && url.Port == 443 &&
            url.UserInfo.Length == 0 && url.Fragment.Length == 0 && url.AbsoluteUri.Length <= 2048 &&
            (IPAddress.TryParse(url.Host, out IPAddress? literal) ? IsPublic(literal) :
                url.Host.Contains('.') && !url.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) &&
                !url.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase));
    }

    public async Task<Outcome<byte[]>> FetchAsync(Uri url, bool consent, CancellationToken cancellationToken)
    {
        if (!consent || !ValidUrl(url))
        {
            return Outcome<byte[]>.Fail(Failure.Validation);
        }

        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        using SocketsHttpHandler? handler = _fixtureTransport is null ? new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            MaxResponseHeadersLength = 16,
            ConnectCallback = async (context, token) =>
                                    {
                                        return await ConnectAsync(context.DnsEndPoint.Host, context.DnsEndPoint.Port, token).ConfigureAwait(false);
                                    }
        } : null;
        using HttpClient client = new HttpClient(_fixtureTransport ?? handler!, disposeHandler: _fixtureTransport is null);
        try
        {
            for (int redirects = 0; redirects <= 3; redirects++)
            {
                if (!ValidUrl(url))
                {
                    return Outcome<byte[]>.Fail(Failure.Validation);
                }

                using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Accept.ParseAdd("image/png");
                using HttpResponseMessage response = await client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    deadline.Token
                ).ConfigureAwait(false);
                if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
                {
                    if (response.Headers.Location is not Uri location || redirects == 3)
                    {
                        return Outcome<byte[]>.Fail(Failure.Validation);
                    }

                    url = location.IsAbsoluteUri ? location : new Uri(url, location);
                    continue;
                }

                if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType != "image/png" ||
                    response.Content.Headers.ContentEncoding.Count > 0 || response.Content.Headers.ContentLength > 4194304)
                {
                    return Outcome<byte[]>.Fail(Failure.Validation);
                }

                await using Stream input = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
                using MemoryStream output = new MemoryStream();
                byte[] buffer = new byte[16384];
                int read;
                while ((read = await input.ReadAsync(buffer, deadline.Token).ConfigureAwait(false)) > 0)
                {
                    if (output.Length + read > 4194304)
                    {
                        return Outcome<byte[]>.Fail(Failure.Validation);
                    }

                    output.Write(buffer, 0, read);
                }

                return PngImageNormalizer.Normalize(output.ToArray());
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Outcome<byte[]>.Fail(Failure.Unavailable);
        }

        return Outcome<byte[]>.Fail(Failure.Validation);
    }

    internal async Task<Stream> ConnectAsync(string host, int port, CancellationToken cancellationToken)
    {
        IPAddress[] addresses = await _dns.ResolveAsync(host, cancellationToken).ConfigureAwait(false);
        if (port != 443 || addresses.Length is 0 or > 16 || addresses.Any(address => !IsPublic(address)))
        {
            throw new IOException("Remote resource destination denied.");
        }

        Socket socket = new Socket(
            Socket.OSSupportsIPv6 ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork,
            SocketType.Stream,
            ProtocolType.Tcp
        );
        try
        {
            if (Socket.OSSupportsIPv6)
            {
                socket.DualMode = true;
            }
            else
            {
                addresses = addresses.Where(address => address.AddressFamily == AddressFamily.InterNetwork).ToArray();
                if (addresses.Length == 0)
                {
                    throw new IOException("Remote resource destination unavailable.");
                }
            }

            await socket.ConnectAsync(addresses, port, cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
