using Postfold.Core.Enums;
using Postfold.Core.Interfaces;
using Postfold.Core.Models.Accounts;
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Mail;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace Postfold.Desktop.Services;

/// <summary>Bounded HTTPS autoconfiguration lookup, preferring provider files over Thunderbird's database.</summary>
public sealed class MailServerDiscovery : IAccountServerDiscovery
{
    private readonly HttpMessageHandler? _fixtureTransport;

    public MailServerDiscovery()
    {
    }

    internal MailServerDiscovery(HttpMessageHandler fixtureTransport)
    {
        _fixtureTransport = fixtureTransport;
    }

    public async Task<AccountServerSettings> DiscoverAsync(string email, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AccountServerSettings result = new AccountServerSettings(null, null);
        if (!MailAddress.TryCreate(email, out MailAddress? address) || address.Address != email)
        {
            return result;
        }

        string domain;
        try
        {
            domain = new IdnMapping().GetAscii(address.Host).ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            return result;
        }
        if (Uri.CheckHostName(domain) != UriHostNameType.Dns || !domain.Contains('.') ||
            !RemoteImageFetcher.ValidUrl(new Uri("https://" + domain + "/")))
        {
            return result;
        }

        RemoteImageFetcher connection = new RemoteImageFetcher();
        using SocketsHttpHandler? handler = _fixtureTransport is null ? new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(2),
            MaxResponseHeadersLength = 16,
            ConnectCallback = (context, token) => new ValueTask<Stream>(
                connection.ConnectAsync(context.DnsEndPoint.Host, context.DnsEndPoint.Port, token)
            )
        } : null;
        using HttpClient client = new HttpClient(_fixtureTransport ?? handler!, disposeHandler: _fixtureTransport is null);
        foreach (string url in new[]
        {
            "https://autoconfig." + domain + "/mail/config-v1.1.xml",
            "https://" + domain + "/.well-known/autoconfig/mail/config-v1.1.xml",
            "https://autoconfig.thunderbird.net/v1.1/" + domain
        })
        {
            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(3));
            try
            {
                using HttpResponseMessage response = await client.GetAsync(
                    url,
                    HttpCompletionOption.ResponseHeadersRead,
                    deadline.Token
                ).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > 65536 ||
                    response.Content.Headers.ContentEncoding.Count > 0)
                {
                    continue;
                }

                await using Stream input = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
                using MemoryStream document = new MemoryStream();
                byte[] buffer = new byte[4096];
                int read;
                while ((read = await input.ReadAsync(buffer, deadline.Token).ConfigureAwait(false)) > 0)
                {
                    if (document.Length + read > 65536)
                    {
                        break;
                    }
                    document.Write(buffer, 0, read);
                }
                if (read > 0)
                {
                    continue;
                }
                document.Position = 0;
                using XmlReader reader = XmlReader.Create(document, new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    MaxCharactersInDocument = 65536
                });
                XDocument configuration = XDocument.Load(reader);
                XElement? provider = configuration.Root?.Elements("emailProvider").FirstOrDefault(item =>
                    item.Elements("domain").Any(value => string.Equals(value.Value.Trim(), domain, StringComparison.OrdinalIgnoreCase)));
                if (configuration.Root?.Name != "clientConfig" || provider is null)
                {
                    continue;
                }
                result = new AccountServerSettings(
                    result.Incoming ?? Find(provider, "incomingServer", "imap", email, domain),
                    result.Outgoing ?? Find(provider, "outgoingServer", "smtp", email, domain)
                );
                if (result.Incoming is not null && result.Outgoing is not null)
                {
                    return result;
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or SocketException or IOException or XmlException or OperationCanceledException)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
        return result;
    }

    private static MailEndpoint? Find(XElement provider, string element, string type, string email, string domain)
    {
        foreach (XElement server in provider.Elements(element).Where(item => (string?)item.Attribute("type") == type))
        {
            string host = (server.Element("hostname")?.Value.Trim() ?? "").Replace("%EMAILDOMAIN%", domain, StringComparison.Ordinal);
            string username = (server.Element("username")?.Value.Trim() ?? "")
                .Replace("%EMAILADDRESS%", email, StringComparison.Ordinal)
                .Replace("%EMAILLOCALPART%", email[..email.LastIndexOf('@')], StringComparison.Ordinal)
                .Replace("%EMAILDOMAIN%", domain, StringComparison.Ordinal);
            string socket = server.Element("socketType")?.Value.Trim() ?? "";
            if (Uri.CheckHostName(host) != UriHostNameType.Dns || host.Length > 253 ||
                !int.TryParse(server.Element("port")?.Value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int port) ||
                port is < 1 or > 65535 || socket is not "SSL" and not "STARTTLS" ||
                !string.Equals(username, email, StringComparison.OrdinalIgnoreCase) ||
                !server.Elements("authentication").Any(item => item.Value.Trim() is "password-cleartext" or "password-encrypted"))
            {
                continue;
            }
            return new MailEndpoint(host, port, socket == "SSL" ? TlsMode.Implicit : TlsMode.RequiredStartTls, email, "");
        }
        return null;
    }
}
