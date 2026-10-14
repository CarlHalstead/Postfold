using NUnit.Framework;
using Postfold.Core.Enums;
using Postfold.Core.Models.Accounts;
using Postfold.Desktop.Services;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Foundation.Tests.Tests.Accounts;

public sealed class MailServerDiscoveryTests
{
    [Test]
    public async Task DiscoverAsync_WhenProviderHasCompatibleSettings_PrefersProviderAndUsesOnlyDomain()
    {
        // Arrange
        using Fixture transport = new Fixture(_ => Response(Configuration()));
        MailServerDiscovery discovery = new MailServerDiscovery(transport);

        // Act
        AccountServerSettings result = await discovery.DiscoverAsync("owner@synthetic.invalid", CancellationToken.None);

        // Assert
        Assert.That(result.Incoming?.Host, Is.EqualTo("imap.synthetic.invalid"));
        Assert.That(result.Incoming?.Port, Is.EqualTo(993));
        Assert.That(result.Incoming?.Tls, Is.EqualTo(TlsMode.Implicit));
        Assert.That(result.Outgoing?.Host, Is.EqualTo("smtp.synthetic.invalid"));
        Assert.That(result.Outgoing?.Port, Is.EqualTo(587));
        Assert.That(result.Outgoing?.Tls, Is.EqualTo(TlsMode.RequiredStartTls));
        Assert.That(transport.Requests, Is.EqualTo(new[] { "https://autoconfig.synthetic.invalid/mail/config-v1.1.xml" }));
    }

    [Test]
    public async Task DiscoverAsync_WhenProviderIsMissing_UsesDatabaseFallback()
    {
        // Arrange
        using Fixture transport = new Fixture(url => url.Host == "autoconfig.thunderbird.net" ?
            Response(Configuration()) : new HttpResponseMessage(HttpStatusCode.NotFound));
        MailServerDiscovery discovery = new MailServerDiscovery(transport);

        // Act
        AccountServerSettings result = await discovery.DiscoverAsync("owner@synthetic.invalid", CancellationToken.None);

        // Assert
        Assert.That(result.Incoming?.Host, Is.EqualTo("imap.synthetic.invalid"));
        Assert.That(result.Outgoing?.Host, Is.EqualTo("smtp.synthetic.invalid"));
        Assert.That(transport.Requests, Is.EqualTo(new[]
        {
            "https://autoconfig.synthetic.invalid/mail/config-v1.1.xml",
            "https://synthetic.invalid/.well-known/autoconfig/mail/config-v1.1.xml",
            "https://autoconfig.thunderbird.net/v1.1/synthetic.invalid"
        }));
    }

    [Test]
    [TestCase(SocketError.NoData, false)]
    [TestCase(SocketError.HostNotFound, false)]
    [TestCase(SocketError.NoData, true)]
    public async Task DiscoverAsync_WhenProviderDnsHasNoRecords_ContinuesToDatabase(SocketError error, bool wrapped)
    {
        // Arrange
        using Fixture transport = new Fixture(url =>
        {
            if (url.Host == "autoconfig.thunderbird.net")
            {
                return Response(Configuration());
            }
            SocketException missing = new SocketException((int)error);
            throw (wrapped ? new HttpRequestException("DNS lookup missed", missing) : missing);
        });
        MailServerDiscovery discovery = new MailServerDiscovery(transport);

        // Act
        AccountServerSettings result = await discovery.DiscoverAsync("owner@synthetic.invalid", CancellationToken.None);

        // Assert
        Assert.That(result.Incoming?.Host, Is.EqualTo("imap.synthetic.invalid"));
        Assert.That(result.Outgoing?.Host, Is.EqualTo("smtp.synthetic.invalid"));
        Assert.That(transport.Requests, Has.Count.EqualTo(3));
    }

    [Test]
    [TestCase("SSL", "%EMAILADDRESS%", "password-cleartext", "0")]
    [TestCase("plain", "%EMAILADDRESS%", "password-cleartext", "993")]
    [TestCase("SSL", "%EMAILLOCALPART%", "password-cleartext", "993")]
    [TestCase("SSL", "%EMAILADDRESS%", "OAuth2", "993")]
    public async Task DiscoverAsync_WhenIncomingSettingsAreIncompatible_LeavesOnlyThatEndpointEmpty(
        string socket,
        string username,
        string authentication,
        string port
    )
    {
        // Arrange
        using Fixture transport = new Fixture(_ => Response(Configuration(socket, username, authentication, port)));
        MailServerDiscovery discovery = new MailServerDiscovery(transport);

        // Act
        AccountServerSettings result = await discovery.DiscoverAsync("owner@synthetic.invalid", CancellationToken.None);

        // Assert
        Assert.That(result.Incoming, Is.Null);
        Assert.That(result.Outgoing?.Host, Is.EqualTo("smtp.synthetic.invalid"));
    }

    [Test]
    [TestCase("not xml")]
    [TestCase("<!DOCTYPE clientConfig [<!ENTITY x SYSTEM 'file:///private'>]><clientConfig>&x;</clientConfig>")]
    [TestCase("<clientConfig><emailProvider><domain>other.invalid</domain></emailProvider></clientConfig>")]
    public async Task DiscoverAsync_WhenDocumentIsInvalid_ReturnsNoSuggestions(string xml)
    {
        // Arrange
        using Fixture transport = new Fixture(_ => Response(xml));
        MailServerDiscovery discovery = new MailServerDiscovery(transport);

        // Act
        AccountServerSettings result = await discovery.DiscoverAsync("owner@synthetic.invalid", CancellationToken.None);

        // Assert
        Assert.That(result, Is.EqualTo(new AccountServerSettings(null, null)));
    }

    [Test]
    public async Task DiscoverAsync_WhenResponsesExceedLimit_ReturnsNoSuggestions()
    {
        // Arrange
        using Fixture transport = new Fixture(_ => Response(new string('x', 65537)));
        MailServerDiscovery discovery = new MailServerDiscovery(transport);

        // Act
        AccountServerSettings result = await discovery.DiscoverAsync("owner@synthetic.invalid", CancellationToken.None);

        // Assert
        Assert.That(result, Is.EqualTo(new AccountServerSettings(null, null)));
    }

    [Test]
    [TestCase(false)]
    [TestCase(true)]
    public async Task DiscoverAsync_WhenNetworkIsUnavailable_ReturnsNoSuggestions(bool timeout)
    {
        // Arrange
        using Fixture transport = new Fixture(_ => throw (timeout ? new OperationCanceledException() : new HttpRequestException()));
        MailServerDiscovery discovery = new MailServerDiscovery(transport);

        // Act
        AccountServerSettings result = await discovery.DiscoverAsync("owner@synthetic.invalid", CancellationToken.None);

        // Assert
        Assert.That(result, Is.EqualTo(new AccountServerSettings(null, null)));
    }

    [Test]
    public async Task DiscoverAsync_WhenResponseRedirects_DoesNotFollowIt()
    {
        // Arrange
        using Fixture transport = new Fixture(_ =>
        {
            HttpResponseMessage response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri("http://127.0.0.1/private");
            return response;
        });
        MailServerDiscovery discovery = new MailServerDiscovery(transport);

        // Act
        AccountServerSettings result = await discovery.DiscoverAsync("owner@synthetic.invalid", CancellationToken.None);

        // Assert
        Assert.That(result, Is.EqualTo(new AccountServerSettings(null, null)));
        Assert.That(transport.Requests, Has.Count.EqualTo(3));
        Assert.That(transport.Requests, Has.None.Contains("127.0.0.1"));
    }

    [Test]
    [TestCase("not an email")]
    [TestCase("owner@localhost")]
    [TestCase("owner@host.local")]
    [TestCase("owner@127.0.0.1")]
    public async Task DiscoverAsync_WhenEmailDomainIsUnsuitable_DoesNotRequestConfiguration(string email)
    {
        // Arrange
        using Fixture transport = new Fixture(_ => Response(Configuration()));
        MailServerDiscovery discovery = new MailServerDiscovery(transport);

        // Act
        AccountServerSettings result = await discovery.DiscoverAsync(email, CancellationToken.None);

        // Assert
        Assert.That(result, Is.EqualTo(new AccountServerSettings(null, null)));
        Assert.That(transport.Requests, Is.Empty);
    }

    [Test]
    public async Task DiscoverAsync_WhenCallerCancels_PropagatesCancellationWithoutRequests()
    {
        // Arrange
        using Fixture transport = new Fixture(_ => Response(Configuration()));
        MailServerDiscovery discovery = new MailServerDiscovery(transport);
        using CancellationTokenSource cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        // Act / Assert
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await discovery.DiscoverAsync(
            "owner@synthetic.invalid",
            cancellation.Token
        ));
        Assert.That(transport.Requests, Is.Empty);
    }

    private static string Configuration(
        string socket = "SSL",
        string username = "%EMAILADDRESS%",
        string authentication = "password-cleartext",
        string port = "993"
    )
    {
        return "<clientConfig version='1.1'><emailProvider id='synthetic.invalid'><domain>synthetic.invalid</domain>" +
            "<incomingServer type='imap'><hostname>imap.%EMAILDOMAIN%</hostname><port>" + port + "</port>" +
            "<socketType>" + socket + "</socketType><username>" + username + "</username>" +
            "<authentication>" + authentication + "</authentication></incomingServer>" +
            "<outgoingServer type='smtp'><hostname>smtp.synthetic.invalid</hostname><port>587</port>" +
            "<socketType>STARTTLS</socketType><username>%EMAILADDRESS%</username>" +
            "<authentication>password-cleartext</authentication></outgoingServer></emailProvider></clientConfig>";
    }

    private static HttpResponseMessage Response(string xml)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(xml)
        };
    }

    private sealed class Fixture(Func<Uri, HttpResponseMessage> respond) : HttpMessageHandler
    {
        internal List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request.RequestUri!.AbsoluteUri);
            return Task.FromResult(respond(request.RequestUri));
        }
    }
}
