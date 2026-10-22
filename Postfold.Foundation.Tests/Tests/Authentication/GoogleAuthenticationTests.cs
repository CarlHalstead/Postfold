using NUnit.Framework;
using Postfold.Core.Enums;
using Postfold.Core.Models;
using Postfold.Core.Models.Accounts;
using Postfold.Desktop.Services.Authentication;
using Postfold.Foundation.Tests.Fixtures.Authentication;
using Postfold.Foundation.Tests.Fixtures.Updates;
using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Foundation.Tests.Tests.Authentication;

[TestFixture]
public sealed class GoogleAuthenticationTests
{
    [Test]
    public async Task AcquireAsync_WhenAuthorized_BindsPkceAndPersistsBeforeReturning()
    {
        // Arrange
        await using AuthenticationFixture fixture = new AuthenticationFixture();
        await using GoogleAccountAuthenticator service = Create(fixture);

        // Act
        Outcome<AccessToken> result = await service.AcquireAsync(true, CancellationToken.None);
        string request = fixture.TokenRequests.Single();
        string verifier = AuthenticationFixture.Query(new Uri("https://fixture.test/?" + request))["code_verifier"];
        string challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        // Assert
        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Value!.Subject, Is.EqualTo(fixture.Subject));
        Assert.That(result.Value.ToString(), Does.Not.Contain("synthetic-access"));
        Assert.That(fixture.Stored.Count, Is.EqualTo(1));
        Assert.That(AuthenticationFixture.Query(fixture.Authorization!)["code_challenge"], Is.EqualTo(challenge));
        Assert.That(AuthenticationFixture.Query(fixture.Authorization!)["scope"], Is.EqualTo(GoogleAccountAuthenticator.Scope));
        Assert.That(request, Does.Not.Contain("client_secret"));
        Assert.That(await fixture.Callback!, Does.Not.Contain("synthetic-code"));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task AcquireAsync_WhenDesktopRegistrationRequiresClientSecret_UsesItForExchangeAndRefresh(bool configured)
    {
        // Arrange
        await using AuthenticationFixture fixture = new AuthenticationFixture
        {
            RequiredGoogleClientSecret = "synthetic-desktop-client-field"
        };
        fixture.TokenJson = fixture.TokenJson.Replace("3600", "30", StringComparison.Ordinal);
        await using GoogleAccountAuthenticator service = new GoogleAccountAuthenticator(
            new AccountId(1),
            "synthetic-client",
            fixture,
            fixture,
            fixture.Http,
            TimeProvider.System,
            TimeSpan.FromSeconds(10),
            clientSecret: configured ? fixture.RequiredGoogleClientSecret : null
        );

        // Act
        Outcome<AccessToken> connected = await service.AcquireAsync(true, CancellationToken.None);
        Outcome<AccessToken> refreshed = await service.AcquireAsync(false, CancellationToken.None);

        // Assert
        Assert.That(connected.IsSuccess, Is.EqualTo(configured));
        Assert.That(refreshed.IsSuccess, Is.EqualTo(configured));
        Assert.That(fixture.Stored.Count, Is.EqualTo(configured ? 1 : 0));
        Assert.That(fixture.TokenRequests.Count, Is.EqualTo(configured ? 2 : 1));
        Assert.That(AuthenticationFixture.Query(fixture.Authorization!).ContainsKey("client_secret"), Is.False);
        if (configured)
        {
            Assert.That(fixture.TokenRequests.All(request =>
                AuthenticationFixture.Query(new Uri("https://fixture.test/?" + request))["client_secret"] == fixture.RequiredGoogleClientSecret), Is.True);
            Assert.That(AuthenticationFixture.Query(new Uri("https://fixture.test/?" + fixture.TokenRequests.Last()))["grant_type"], Is.EqualTo("refresh_token"));
        }
        else
        {
            Assert.That(connected.Failure, Is.EqualTo(Failure.Validation));
            Assert.That(refreshed.Failure, Is.EqualTo(Failure.NeedsAuthentication));
        }
    }

    [TestCase("code=x")]
    [TestCase("code=x&state=wrong")]
    [TestCase("code=x&state={state}&state={state}")]
    [TestCase("code=x&code=y&state={state}")]
    [TestCase("error=access_denied&code=x&state={state}")]
    [TestCase("error=access_denied&state=forged")]
    [TestCase("code=x&state={state}&iss=https%3A%2F%2Fevil.test")]
    [TestCase("code=&state={state}")]
    public async Task AcquireAsync_WhenCallbackIsHostile_DoesNotExchange(string query)
    {
        // Arrange
        await using AuthenticationFixture fixture = new AuthenticationFixture { CallbackQuery = query };
        await using GoogleAccountAuthenticator service = Create(fixture);

        // Act
        Outcome<AccessToken> result = await service.AcquireAsync(true, CancellationToken.None);

        // Assert
        Assert.That(result.Failure, Is.EqualTo(Failure.Validation));
        Assert.That(fixture.TokenRequests, Is.Empty);
        Assert.That(fixture.Stored, Is.Empty);
    }

    [TestCase("evil.test", "/")]
    [TestCase("127.0.0.1:1", "/")]
    [TestCase(null, "/wrong")]
    public async Task AcquireAsync_WhenCallbackEndpointDiffers_Rejects(string? host, string path)
    {
        // Arrange
        await using AuthenticationFixture fixture = new AuthenticationFixture { CallbackHost = host, CallbackPath = path };
        await using GoogleAccountAuthenticator service = Create(fixture);

        // Act
        Outcome<AccessToken> result = await service.AcquireAsync(true, CancellationToken.None);

        // Assert
        Assert.That(result.Failure, Is.EqualTo(Failure.Validation));
        Assert.That(fixture.TokenRequests, Is.Empty);
    }

    [Test]
    public async Task ReceiveAsync_WhenReplayed_RejectsAndClosesListener()
    {
        // Arrange
        await using AuthenticationFixture fixture = new AuthenticationFixture();
        using GoogleAuthorizationAttempt attempt = new GoogleAuthorizationAttempt();

        // Act
        Outcome<string> first = await attempt.ReceiveAsync(fixture, "synthetic", CancellationToken.None);
        Outcome<string> second = await attempt.ReceiveAsync(fixture, "synthetic", CancellationToken.None);
        using TcpClient replay = new TcpClient();

        // Assert
        Assert.That(first.IsSuccess, Is.True);
        Assert.That(second.Failure, Is.EqualTo(Failure.Validation));
        await Assert.ThrowsAsync<SocketException>(async () => await replay.ConnectAsync(IPAddress.Loopback, attempt.Redirect.Port));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task AcquireAsync_WhenCanceledOrShutdown_ClosesOwnedListener(bool shutdown)
    {
        // Arrange
        await using AuthenticationFixture fixture = new AuthenticationFixture { SendCallback = false };
        await using GoogleAccountAuthenticator service = Create(fixture);
        using CancellationTokenSource canceled = new CancellationTokenSource();
        Task<Outcome<AccessToken>> pending = service.AcquireAsync(true, canceled.Token);
        Uri authorization = await fixture.Opened.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Uri redirect = new Uri(AuthenticationFixture.Query(authorization)["redirect_uri"]);

        // Act
        if (shutdown)
        {
            await service.DisposeAsync();
        }
        else
        {
            await canceled.CancelAsync();
        }

        // Assert
        await Assert.ThatAsync(async () => await pending, Throws.InstanceOf<OperationCanceledException>());
        using TcpClient probe = new TcpClient();
        await Assert.ThrowsAsync<SocketException>(async () => await probe.ConnectAsync(IPAddress.Loopback, redirect.Port));
        Assert.That(fixture.Stored, Is.Empty);
    }

    [TestCase(Failure.Unavailable)]
    [TestCase(Failure.PolicyBlocked)]
    public async Task AcquireAsync_WhenBrowserFails_DoesNotExchange(Failure failure)
    {
        // Arrange
        await using AuthenticationFixture fixture = new AuthenticationFixture { BrowserFailure = failure };
        await using GoogleAccountAuthenticator service = Create(fixture);

        // Act
        Outcome<AccessToken> result = await service.AcquireAsync(true, CancellationToken.None);

        // Assert
        Assert.That(result.Failure, Is.EqualTo(failure));
        Assert.That(fixture.TokenRequests, Is.Empty);
    }

    [TestCase("{\"error\":\"invalid_grant\"}", Failure.NeedsAuthentication)]
    [TestCase("{\"error\":\"admin_policy_enforced\"}", Failure.PolicyBlocked)]
    public async Task AcquireAsync_WhenRefreshDenied_PreservesCacheAndSafeClassification(string error, Failure expected)
    {
        // Arrange
        await using AuthenticationFixture fixture = new AuthenticationFixture();
        await using GoogleAccountAuthenticator service = Create(fixture, TimeSpan.FromMinutes(1));
        fixture.TokenJson = fixture.TokenJson.Replace("3600", "1", StringComparison.Ordinal);
        await service.AcquireAsync(true, CancellationToken.None);
        byte[] before = fixture.Stored.Single().Value.ToArray();
        fixture.TokenStatus = HttpStatusCode.BadRequest;
        fixture.TokenJson = error;

        // Act
        Outcome<AccessToken> result = await service.AcquireAsync(false, CancellationToken.None);

        // Assert
        Assert.That(result.Failure, Is.EqualTo(expected));
        Assert.That(fixture.Stored.Single().Value, Is.EqualTo(before));
    }

    [Test]
    public async Task AcquireAsync_WhenRefreshOmitsRotation_RetainsRefreshAndSerializesAcquisition()
    {
        // Arrange
        await using AuthenticationFixture fixture = new AuthenticationFixture();
        await using GoogleAccountAuthenticator service = Create(fixture);
        fixture.TokenJson = fixture.TokenJson.Replace("3600", "1", StringComparison.Ordinal);
        await service.AcquireAsync(true, CancellationToken.None);
        fixture.TokenJson = fixture.TokenJson.Replace("1,", "3600,", StringComparison.Ordinal)
            .Replace("\"refresh_token\":\"synthetic-refresh\",", "", StringComparison.Ordinal);

        // Act
        Outcome<AccessToken>[] results = await Task.WhenAll(service.AcquireAsync(false, CancellationToken.None), service.AcquireAsync(false, CancellationToken.None));

        // Assert
        Assert.That(results.All(result => result.IsSuccess), Is.True);
        Assert.That(fixture.TokenRequests.Count, Is.EqualTo(2));
        Assert.That(fixture.TokenRequests.Last(), Does.Contain("refresh_token=synthetic-refresh"));
        Assert.That(Encoding.UTF8.GetString(fixture.Stored.Single().Value), Does.Contain("synthetic-refresh"));
    }

    [Test]
    public async Task AcquireAsync_WhenConsentPartialOrIdentityChanged_PreservesBinding()
    {
        // Arrange
        await using AuthenticationFixture fixture = new AuthenticationFixture();
        await using GoogleAccountAuthenticator service = Create(fixture);
        await service.AcquireAsync(true, CancellationToken.None);
        byte[] before = fixture.Stored.Single().Value.ToArray();
        fixture.Subject = "other@example.test";

        // Act
        Outcome<AccessToken> changed = await service.AcquireAsync(true, CancellationToken.None);
        fixture.TokenJson = fixture.TokenJson.Replace("gmail.modify", "gmail.readonly", StringComparison.Ordinal);
        Outcome<AccessToken> partial = await service.AcquireAsync(true, CancellationToken.None);

        // Assert
        Assert.That(changed.Failure, Is.EqualTo(Failure.NeedsReview));
        Assert.That(partial.Failure, Is.EqualTo(Failure.NeedsAuthentication));
        Assert.That(fixture.Stored.Single().Value, Is.EqualTo(before));
    }

    [Test]
    public async Task RevokeAsync_WhenExplicit_RemovesOnlyLocalGrantAfterProviderSuccess()
    {
        // Arrange
        await using AuthenticationFixture fixture = new AuthenticationFixture();
        await using GoogleAccountAuthenticator service = Create(fixture);
        await service.AcquireAsync(true, CancellationToken.None);
        fixture.Stored["other-account"] = [42];

        // Act
        Outcome<bool> result = await service.RevokeAsync(CancellationToken.None);
        Outcome<AccessToken> silent = await service.AcquireAsync(false, CancellationToken.None);

        // Assert
        Assert.That(result.IsSuccess, Is.True);
        Assert.That(silent.Failure, Is.EqualTo(Failure.NeedsAuthentication));
        Assert.That(fixture.Stored.Keys, Is.EqualTo(new[] { "other-account" }));
    }

    [Test]
    public async Task AcquireAsync_WhenAttemptExpires_StopsAtControlledDeadline()
    {
        // Arrange
        await using AuthenticationFixture fixture = new AuthenticationFixture { SendCallback = false };
        UpdatePolicyClock clock = new UpdatePolicyClock();
        await using GoogleAccountAuthenticator service = new GoogleAccountAuthenticator(new AccountId(1), "synthetic-client",
            fixture, fixture, fixture.Http, clock, TimeSpan.FromMinutes(1));
        Task<Outcome<AccessToken>> pending = service.AcquireAsync(true, CancellationToken.None);
        Uri authorization = await fixture.Opened.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Act
        clock.Advance(TimeSpan.FromSeconds(59));
        bool beforeDeadline = pending.IsCompleted;
        clock.Advance(TimeSpan.FromSeconds(1));

        // Assert
        Assert.That(beforeDeadline, Is.False);
        await Assert.ThatAsync(async () => await pending, Throws.InstanceOf<OperationCanceledException>());
        using TcpClient probe = new TcpClient();
        Uri redirect = new Uri(AuthenticationFixture.Query(authorization)["redirect_uri"]);
        await Assert.ThrowsAsync<SocketException>(async () => await probe.ConnectAsync(IPAddress.Loopback, redirect.Port));
    }

    [TestCase(Failure.CredentialStorageUnavailable)]
    [TestCase(Failure.Corrupt)]
    public async Task AcquireAsync_WhenStoreUnreadable_DoesNotOpenBrowserOrUseSessionFallback(Failure failure)
    {
        // Arrange
        await using AuthenticationFixture fixture = new AuthenticationFixture { StoreFailure = failure };
        await using GoogleAccountAuthenticator service = Create(fixture);

        // Act
        Outcome<AccessToken> result = await service.AcquireAsync(true, CancellationToken.None);

        // Assert
        Assert.That(result.Failure, Is.EqualTo(failure));
        Assert.That(fixture.Authorization, Is.Null);
        Assert.That(fixture.TokenRequests, Is.Empty);
    }

    [Test]
    public async Task AcquireAsync_WhenRotationCannotPersist_DoesNotReturnUncommittedToken()
    {
        // Arrange
        await using AuthenticationFixture fixture = new AuthenticationFixture();
        await using GoogleAccountAuthenticator service = Create(fixture);
        fixture.TokenJson = fixture.TokenJson.Replace("3600", "1", StringComparison.Ordinal);
        await service.AcquireAsync(true, CancellationToken.None);
        byte[] before = fixture.Stored.Single().Value.ToArray();
        fixture.WriteFailure = Failure.CredentialStorageUnavailable;
        fixture.TokenJson = fixture.TokenJson.Replace("synthetic-refresh", "rotated-refresh", StringComparison.Ordinal);

        // Act
        Outcome<AccessToken> result = await service.AcquireAsync(false, CancellationToken.None);

        // Assert
        Assert.That(result.Failure, Is.EqualTo(Failure.CredentialStorageUnavailable));
        Assert.That(result.Value, Is.Null);
        Assert.That(fixture.Stored.Single().Value, Is.EqualTo(before));
    }

    [Test]
    public async Task AcquireAsync_WhenRotatedAndReopened_UsesNewRefreshToken()
    {
        // Arrange
        await using AuthenticationFixture fixture = new AuthenticationFixture();
        await using GoogleAccountAuthenticator service = Create(fixture);
        fixture.TokenJson = fixture.TokenJson.Replace("3600", "1", StringComparison.Ordinal);
        await service.AcquireAsync(true, CancellationToken.None);
        fixture.TokenJson = fixture.TokenJson.Replace("synthetic-refresh", "rotated-refresh", StringComparison.Ordinal);
        await service.AcquireAsync(false, CancellationToken.None);
        await using GoogleAccountAuthenticator reopened = Create(fixture);

        // Act
        Outcome<AccessToken> result = await reopened.AcquireAsync(false, CancellationToken.None);

        // Assert
        Assert.That(result.IsSuccess, Is.True);
        Assert.That(fixture.TokenRequests.Last(), Does.Contain("refresh_token=rotated-refresh"));
    }

    [Test]
    public async Task AcquireAsync_WhenCacheDeleted_StillEnforcesHostBindingOnReconnect()
    {
        // Arrange
        await using AuthenticationFixture fixture = new AuthenticationFixture();
        await using GoogleAccountAuthenticator service = new GoogleAccountAuthenticator(new AccountId(1), "synthetic-client", fixture,
            fixture, fixture.Http, TimeProvider.System, TimeSpan.FromSeconds(10), "expected@example.test");

        // Act
        Outcome<AccessToken> result = await service.AcquireAsync(true, CancellationToken.None);

        // Assert
        Assert.That(result.Failure, Is.EqualTo(Failure.NeedsReview));
        Assert.That(fixture.Stored, Is.Empty);
    }

    [Test]
    public async Task DeleteCredentialsAsync_WhenReconnected_PreservesOwnedAccountBinding()
    {
        // Arrange
        await using AuthenticationFixture fixture = new AuthenticationFixture();
        await using GoogleAccountAuthenticator service = Create(fixture);
        await service.AcquireAsync(true, CancellationToken.None);
        await service.DeleteCredentialsAsync(CancellationToken.None);
        fixture.Subject = "other@example.test";

        // Act
        Outcome<AccessToken> result = await service.AcquireAsync(true, CancellationToken.None);

        // Assert
        Assert.That(result.Failure, Is.EqualTo(Failure.NeedsReview));
        Assert.That(fixture.Stored, Is.Empty);
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task AcquireAsync_WhenCodeOrPkceBelongsToAnotherAttempt_DoesNotStoreGrant(bool substitutedCode)
    {
        // Arrange
        await using AuthenticationFixture fixture = new AuthenticationFixture();
        if (substitutedCode)
        {
            fixture.CallbackQuery = "code=other-attempt-code&state={state}";
        }
        else
        {
            fixture.CodeChallengeOverride = "other-attempt-challenge";
        }

        await using GoogleAccountAuthenticator service = Create(fixture);

        // Act
        Outcome<AccessToken> result = await service.AcquireAsync(true, CancellationToken.None);

        // Assert
        Assert.That(result.Failure, Is.EqualTo(Failure.NeedsAuthentication));
        Assert.That(fixture.Stored, Is.Empty);
    }

    [Test]
    public async Task AcquireAsync_WhenTokenResponseOversized_StopsBeforePersistence()
    {
        // Arrange
        await using AuthenticationFixture fixture = new AuthenticationFixture();
        fixture.TokenJson = fixture.TokenJson.Replace("synthetic-access", new string('x', 70000), StringComparison.Ordinal);
        await using GoogleAccountAuthenticator service = Create(fixture);

        // Act
        Outcome<AccessToken> result = await service.AcquireAsync(true, CancellationToken.None);

        // Assert
        Assert.That(result.IsSuccess, Is.False);
        Assert.That(fixture.Stored, Is.Empty);
    }

    private static GoogleAccountAuthenticator Create(AuthenticationFixture fixture, TimeSpan? timeout = null)
    {
        return new GoogleAccountAuthenticator(new AccountId(1), "synthetic-client", fixture, fixture, fixture.Http, TimeProvider.System,
            timeout ?? TimeSpan.FromSeconds(10));
    }
}
