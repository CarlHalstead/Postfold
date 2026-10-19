using NUnit.Framework;
using Postfold.Core.Enums;
using Postfold.Core.Models;
using Postfold.Desktop.Services.Updates;
using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Foundation.Tests.Tests.Updates;

public sealed class UpdateTransportTests
{
    [TestCase("http://github.com/CarlHalstead/Postfold/releases/download/v1/")]
    [TestCase("https://other.invalid/CarlHalstead/Postfold/releases/download/v1/")]
    [TestCase("https://github.com/other/Postfold/releases/download/v1/")]
    [TestCase("https://github.com/CarlHalstead/Postfold/releases/latest/")]
    [TestCase("https://github.com/CarlHalstead/Postfold/releases/download/v1/nested/")]
    [TestCase("https://github.com/CarlHalstead/Postfold/releases/download/v1/?hint=key")]
    [TestCase("https://github.com/CarlHalstead/Postfold/releases/download/v..1/")]
    [TestCase("https://raw.githubusercontent.com/CarlHalstead/Postfold/main/updates/win-x64-beta.json?key=unsigned")]
    [TestCase("https://raw.githubusercontent.com/other/Postfold/main/updates/win-x64-beta.json")]
    public void Construct_WhenLocationIsNotAnImmutableApprovedRepository_RejectsIt(string uri)
    {
        // Arrange / Act / Assert
        Assert.Throws<ArgumentException>(() => new HttpsUpdateTransport(new Uri(uri), TimeProvider.System));
    }

    [TestCase("http://release-assets.githubusercontent.com/asset")]
    [TestCase("https://other.invalid/asset")]
    [TestCase("https://user@release-assets.githubusercontent.com/asset")]
    [TestCase("https://release-assets.githubusercontent.com:444/asset")]
    public async Task FetchAsync_WhenRedirectCrossesPolicy_RejectsBeforeFollowing(string location)
    {
        // Arrange
        FixtureHandler handler = new FixtureHandler(_ =>
        {
            HttpResponseMessage response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri(location);
            return response;
        });
        using HttpsUpdateTransport transport = new HttpsUpdateTransport(Location(), TimeProvider.System, handler);

        // Act
        Outcome<byte[]> result = await transport.FetchAsync("manifest.json", 64, CancellationToken.None);

        // Assert
        Assert.That(result.Failure, Is.EqualTo(Failure.Validation));
        Assert.That(handler.Requests, Is.EqualTo(1));
    }

    [Test]
    public async Task FetchAsync_WhenActualBodyExceedsDeclaredHeader_EnforcesStreamBound()
    {
        // Arrange
        FixtureHandler handler = new FixtureHandler(_ =>
        {
            HttpResponseMessage response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[65])
            };
            response.Content.Headers.ContentLength = 1;
            return response;
        });
        using HttpsUpdateTransport transport = new HttpsUpdateTransport(Location(), TimeProvider.System, handler);

        // Act
        Outcome<byte[]> result = await transport.FetchAsync("manifest.json", 64, CancellationToken.None);

        // Assert
        Assert.That(result.Failure, Is.EqualTo(Failure.Validation));
        Assert.That(result.Value, Is.Null);
    }

    [Test]
    public async Task FetchAsync_WhenRateLimited_RespectsBoundedRetryAfterWithControlledTime()
    {
        // Arrange
        FixtureClock clock = new FixtureClock();
        FixtureHandler handler = new FixtureHandler(_ =>
        {
            HttpResponseMessage response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromDays(1));
            return response;
        });
        using HttpsUpdateTransport transport = new HttpsUpdateTransport(Location(), clock, handler);

        // Act
        Outcome<byte[]> first = await transport.FetchAsync("manifest.json", 64, CancellationToken.None);
        Outcome<byte[]> blocked = await transport.FetchAsync("manifest.json", 64, CancellationToken.None);
        clock.Now = transport.RetryAt;
        Outcome<byte[]> retried = await transport.FetchAsync("manifest.json", 64, CancellationToken.None);

        // Assert
        Assert.That(first.Failure, Is.EqualTo(Failure.Transient));
        Assert.That(blocked.Failure, Is.EqualTo(Failure.Transient));
        Assert.That(retried.Failure, Is.EqualTo(Failure.Transient));
        Assert.That(handler.Requests, Is.EqualTo(2));
        Assert.That(transport.RateLimited, Is.True);
        Assert.That(transport.RetryAt - clock.Now, Is.EqualTo(TimeSpan.FromHours(1)));
    }

    [Test]
    public async Task FetchAsync_WhenServerRedirectsRepeatedly_StopsAtBound()
    {
        // Arrange
        FixtureHandler handler = new FixtureHandler(_ =>
        {
            HttpResponseMessage response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri("https://release-assets.githubusercontent.com/fixture");
            return response;
        });
        using HttpsUpdateTransport transport = new HttpsUpdateTransport(Location(), TimeProvider.System, handler);

        // Act
        Outcome<byte[]> result = await transport.FetchAsync("manifest.json", 64, CancellationToken.None);

        // Assert
        Assert.That(result.Failure, Is.EqualTo(Failure.Validation));
        Assert.That(handler.Requests, Is.EqualTo(5));
    }

    [Test]
    public async Task DiscoverLocationAsync_WhenCatalogueRedirects_RefusesBeforeFollowingEvenAnAllowedAssetHost()
    {
        // Arrange
        FixtureHandler handler = new FixtureHandler(_ =>
        {
            HttpResponseMessage response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri("https://release-assets.githubusercontent.com/fixture");
            return response;
        });
        using HttpsUpdateTransport transport = new HttpsUpdateTransport(new Uri(ProductionUpdateIdentity.Discovery), TimeProvider.System, handler);

        // Act
        Failure result = await transport.DiscoverLocationAsync(CancellationToken.None);

        // Assert
        Assert.That(result, Is.EqualTo(Failure.Validation));
        Assert.That(handler.Requests, Is.EqualTo(1));
    }

    private static Uri Location()
    {
        return new Uri("https://github.com/CarlHalstead/Postfold/releases/download/synthetic-v1/");
    }

    private sealed class FixtureClock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = new DateTimeOffset(2026, 10, 15, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow()
        {
            return Now;
        }
    }

    private sealed class FixtureHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        internal int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(response(request));
        }
    }
}
