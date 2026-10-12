using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Postfold.Core;
using Postfold.Desktop;

namespace Postfold.Foundation.Tests;

public sealed class RemoteImageSecurityTests
{
    [TestCase("https://127.0.0.1/pixel")]
    [TestCase("https://[::1]/pixel")]
    [TestCase("https://192.168.1.1/pixel")]
    [TestCase("https://user:password@synthetic.invalid/pixel")]
    [TestCase("http://synthetic.invalid/pixel")]
    public async Task FetchAsync_WhenRedirectTargetsUnsafeDestination_NeverMakesSecondRequest(string target)
    {
        // Arrange
        using RedirectFixture transport = new RedirectFixture(new Uri(target));
        RemoteImageFetcher fetcher = new RemoteImageFetcher(transport);

        // Act
        Outcome<byte[]> result = await fetcher.FetchAsync(new Uri("https://synthetic.invalid/pixel"), true,
            CancellationToken.None);

        // Assert
        Assert.That(result.Failure, Is.EqualTo(Failure.Validation));
        Assert.That(transport.Requests, Is.EqualTo(1));
        Assert.That(transport.PrivateHeadersAbsent, Is.True);
    }

    [Test]
    public async Task FetchAsync_WhenConsentIsAbsent_NeverInvokesTransport()
    {
        // Arrange
        using RedirectFixture transport = new RedirectFixture(new Uri("https://synthetic.invalid/second"));

        // Act
        Outcome<byte[]> result = await new RemoteImageFetcher(transport).FetchAsync(
            new Uri("https://synthetic.invalid/pixel"), false, CancellationToken.None);

        // Assert
        Assert.That(result.Failure, Is.EqualTo(Failure.Validation));
        Assert.That(transport.Requests, Is.Zero);
    }

    [Test]
    public async Task ConnectAsync_WhenDnsRebindsToPrivateAddress_RechecksBeforeCreatingSocket()
    {
        // Arrange
        RebindingDns resolver = new RebindingDns();
        IPAddress[] previouslyPublic = await resolver.ResolveAsync("synthetic.invalid", CancellationToken.None);
        RemoteImageFetcher fetcher = new RemoteImageFetcher(resolver);
        Assert.That(RemoteImageFetcher.IsPublic(previouslyPublic[0]), Is.True);
        await

                // Act / Assert
                Assert.ThrowsAsync<IOException>(async () => await fetcher.ConnectAsync("synthetic.invalid", 443,
            CancellationToken.None));
        Assert.That(resolver.Lookups, Is.EqualTo(2));
    }

    [TestCase("2001:4860:4860::8888", true)]
    [TestCase("2606:4700:4700::1111", true)]
    [TestCase("2001:db8::1", false)]
    [TestCase("2002:7f00:1::1", false)]
    [TestCase("2001::1", false)]
    [TestCase("3fff:fff::1", false)]
    [TestCase("fc00::1", false)]
    [TestCase("fe80::1", false)]
    [TestCase("64:ff9b::7f00:1", false)]
    public void IsPublic_WhenIpv6RangeIsClassified_RejectsSpecialAndTransitionAddresses(string value, bool expected)
    {
        // Arrange
        IPAddress address = IPAddress.Parse(value);

        // Act
        bool allowed = RemoteImageFetcher.IsPublic(address);

        // Assert
        Assert.That(allowed, Is.EqualTo(expected));
    }

    private sealed class RedirectFixture : HttpMessageHandler
    {
        private readonly Uri _target;

        internal RedirectFixture(Uri target)
        {
            _target = target;
        }

        internal int Requests
        {
            get; private set;
        }
        internal bool PrivateHeadersAbsent
        {
            get; private set;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            PrivateHeadersAbsent = request.Headers.Authorization is null && request.Headers.Referrer is null &&
                !request.Headers.Contains("Cookie") && !request.Headers.Contains("Proxy-Authorization");
            HttpResponseMessage response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = _target;
            return Task.FromResult(response);
        }
    }

    private sealed class RebindingDns : IDnsResolver
    {
        internal int Lookups
        {
            get; private set;
        }

        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken)
        {
            Lookups++;
            return Task.FromResult(new[] { IPAddress.Parse(Lookups == 1 ? "8.8.8.8" : "127.0.0.1") });
        }
    }
}
