using Microsoft.Identity.Client;
using NUnit.Framework;
using Postfold.Core.Enums;
using Postfold.Core.Models;
using Postfold.Core.Models.Accounts;
using Postfold.Desktop.Services.Authentication;
using Postfold.Foundation.Tests.Fixtures.Authentication;
using Postfold.Foundation.Tests.Fixtures.Updates;
using System;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Foundation.Tests.Tests.Authentication;

[TestFixture]
public sealed class MicrosoftAuthenticationTests
{
    [Test]
    public async Task AcquireAsync_WhenAuthorized_PersistsMsalCacheAndRestoresSilently()
    {
        // Arrange
        await using MicrosoftAuthenticationFixture fixture = new MicrosoftAuthenticationFixture();
        await using MicrosoftAccountAuthenticator service = Create(fixture);

        // Act
        Outcome<AccessToken> first = await service.AcquireAsync(true, CancellationToken.None);
        await using MicrosoftAccountAuthenticator reopened = Create(fixture);
        Outcome<AccessToken> restored = await reopened.AcquireAsync(false, CancellationToken.None);

        // Assert
        Assert.That(first.Failure, Is.EqualTo(Failure.None));
        Assert.That(restored.Failure, Is.EqualTo(Failure.None));
        Assert.That(restored.Value!.Subject, Is.EqualTo(MicrosoftAuthenticationFixture.User + "." + MicrosoftAuthenticationFixture.Tenant));
        Assert.That(restored.Value.Tenant, Is.EqualTo(MicrosoftAuthenticationFixture.Tenant));
        Assert.That(fixture.Exchanges, Is.EqualTo(1));
        Assert.That(fixture.LastRequest, Does.Contain("code_verifier="));
        Assert.That(fixture.LastRequest, Does.Not.Contain("client_secret"));
        Assert.That(AuthenticationFixture.Query(fixture.Shared.Authorization!)["scope"], Does.Contain("offline_access"));
        Assert.That(fixture.Shared.Stored.Count, Is.EqualTo(1));
    }

    [Test]
    public async Task AcquireAsync_WhenPartialOrDifferentAccount_DoesNotReplaceCache()
    {
        // Arrange
        await using MicrosoftAuthenticationFixture fixture = new MicrosoftAuthenticationFixture();
        await using MicrosoftAccountAuthenticator service = Create(fixture);
        Assert.That((await service.AcquireAsync(true, CancellationToken.None)).IsSuccess, Is.True);
        byte[] before = fixture.Shared.Stored.Single().Value.ToArray();
        fixture.GrantedScopes = "https://outlook.office.com/IMAP.AccessAsUser.All";

        // Act
        Outcome<AccessToken> partial = await service.AcquireAsync(true, CancellationToken.None);
        fixture.GrantedScopes += " https://outlook.office.com/SMTP.Send";
        fixture.HomeUser = "44444444-4444-4444-4444-444444444444";
        Outcome<AccessToken> changed = await service.AcquireAsync(true, CancellationToken.None);

        // Assert
        Assert.That(partial.Failure, Is.EqualTo(Failure.NeedsAuthentication));
        Assert.That(changed.Failure, Is.EqualTo(Failure.NeedsReview));
        Assert.That(fixture.Shared.Stored.Single().Value, Is.EqualTo(before));
    }

    [TestCase("invalid_grant", Failure.NeedsAuthentication)]
    [TestCase("access_denied", Failure.PolicyBlocked)]
    [TestCase("unauthorized_client", Failure.PolicyBlocked)]
    public async Task AcquireAsync_WhenProviderRejects_ReturnsOnlySafeOutcome(string error, Failure expected)
    {
        // Arrange
        await using MicrosoftAuthenticationFixture fixture = new MicrosoftAuthenticationFixture { Error = error };
        await using MicrosoftAccountAuthenticator service = Create(fixture);

        // Act
        Outcome<AccessToken> result = await service.AcquireAsync(true, CancellationToken.None);

        // Assert
        Assert.That(result.Failure, Is.EqualTo(expected));
        Assert.That(result.ToString(), Does.Not.Contain("synthetic-private-error"));
        Assert.That(fixture.Shared.Stored, Is.Empty);
    }

    [Test]
    public async Task DeleteCredentialsAsync_WhenRequested_DoesNotClaimProviderRevocation()
    {
        // Arrange
        await using MicrosoftAuthenticationFixture fixture = new MicrosoftAuthenticationFixture();
        await using MicrosoftAccountAuthenticator service = Create(fixture);
        await service.AcquireAsync(true, CancellationToken.None);
        int before = fixture.Exchanges;

        // Act
        Outcome<bool> deleted = await service.DeleteCredentialsAsync(CancellationToken.None);
        Outcome<AccessToken> silent = await service.AcquireAsync(false, CancellationToken.None);

        // Assert
        Assert.That(deleted.IsSuccess, Is.True);
        Assert.That(silent.Failure, Is.EqualTo(Failure.NeedsAuthentication));
        Assert.That(fixture.Exchanges, Is.EqualTo(before));
        Assert.That(service, Is.Not.InstanceOf<Postfold.Core.Interfaces.IAuthorizationRevoker>());
    }

    [Test]
    public void Classify_WhenClaimsOrNestedSecretsExist_ExposesOnlyClassification()
    {
        // Arrange
        MsalUiRequiredException exception = new MsalUiRequiredException("invalid_grant", "synthetic-access",
            new InvalidOperationException("synthetic-refresh"));

        // Act
        Failure result = MicrosoftAccountAuthenticator.Classify(exception);

        // Assert
        Assert.That(result, Is.EqualTo(Failure.NeedsAuthentication));
        Assert.That(result.ToString(), Does.Not.Contain("synthetic"));
    }

    [Test]
    public async Task AcquireAsync_WhenExpired_RefreshesWithMsalAndRetainsAccountCache()
    {
        // Arrange
        await using MicrosoftAuthenticationFixture fixture = new MicrosoftAuthenticationFixture { ExpiresIn = 30 };
        await using MicrosoftAccountAuthenticator service = Create(fixture);
        Outcome<AccessToken> initial = await service.AcquireAsync(true, CancellationToken.None);
        fixture.ExpiresIn = 3600;
        await using MicrosoftAccountAuthenticator reopened = Create(fixture);

        // Act
        Outcome<AccessToken> refreshed = await reopened.AcquireAsync(false, CancellationToken.None);

        // Assert
        Assert.That(initial.IsSuccess, Is.True);
        Assert.That(refreshed.IsSuccess, Is.True);
        Assert.That(fixture.Exchanges, Is.EqualTo(2));
        Assert.That(fixture.LastRequest, Does.Contain("grant_type=refresh_token"));
        Assert.That(fixture.LastRequest, Does.Contain("refresh_token=synthetic-microsoft-refresh"));
    }

    [TestCase(Failure.CredentialStorageUnavailable)]
    [TestCase(Failure.Corrupt)]
    public async Task AcquireAsync_WhenStoreFails_DoesNotStartSignIn(Failure failure)
    {
        // Arrange
        await using MicrosoftAuthenticationFixture fixture = new MicrosoftAuthenticationFixture();
        fixture.Shared.StoreFailure = failure;
        await using MicrosoftAccountAuthenticator service = Create(fixture);

        // Act
        Outcome<AccessToken> result = await service.AcquireAsync(true, CancellationToken.None);

        // Assert
        Assert.That(result.Failure, Is.EqualTo(failure));
        Assert.That(fixture.Shared.Authorization, Is.Null);
        Assert.That(fixture.Exchanges, Is.Zero);
    }

    [Test]
    public async Task AcquireAsync_WhenSecureWriteFails_DoesNotReturnSessionToken()
    {
        // Arrange
        await using MicrosoftAuthenticationFixture fixture = new MicrosoftAuthenticationFixture();
        fixture.Shared.WriteFailure = Failure.CredentialStorageUnavailable;
        await using MicrosoftAccountAuthenticator service = Create(fixture);

        // Act
        Outcome<AccessToken> result = await service.AcquireAsync(true, CancellationToken.None);

        // Assert
        Assert.That(result.Failure, Is.EqualTo(Failure.CredentialStorageUnavailable));
        Assert.That(result.Value, Is.Null);
        Assert.That(fixture.Shared.Stored, Is.Empty);
    }

    [Test]
    public async Task AcquireAsync_WhenDeadlineOrShutdown_ObservesCancellation()
    {
        // Arrange
        await using MicrosoftAuthenticationFixture fixture = new MicrosoftAuthenticationFixture();
        fixture.Shared.SendCallback = false;
        UpdatePolicyClock clock = new UpdatePolicyClock();
        await using MicrosoftAccountAuthenticator service = new MicrosoftAccountAuthenticator(new Postfold.Core.Models.Accounts.AccountId(1),
            MicrosoftAuthenticationFixture.ClientId, fixture.Shared, fixture.Shared, clock, TimeSpan.FromMinutes(1))
        {
            HttpFactory = fixture
        };
        Task<Outcome<AccessToken>> pending = service.AcquireAsync(true, CancellationToken.None);
        Uri authorization = await fixture.Shared.Opened.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Act
        clock.Advance(TimeSpan.FromMinutes(1));
        await service.DisposeAsync();

        // Assert
        await Assert.ThatAsync(async () => await pending, Throws.InstanceOf<OperationCanceledException>());
        using TcpClient probe = new TcpClient();
        Uri redirect = new Uri(AuthenticationFixture.Query(authorization)["redirect_uri"]);
        await Assert.ThrowsAsync<SocketException>(async () => await probe.ConnectAsync(redirect.DnsSafeHost, redirect.Port));
        Assert.That(fixture.Exchanges, Is.Zero);
        Assert.That(fixture.Shared.Stored, Is.Empty);
    }

    [TestCase("code=x&state=forged")]
    [TestCase("error=access_denied&state=forged")]
    public async Task AcquireAsync_WhenStateForged_DoesNotExchange(string query)
    {
        // Arrange
        await using MicrosoftAuthenticationFixture fixture = new MicrosoftAuthenticationFixture();
        fixture.Shared.CallbackQuery = query;
        fixture.Shared.CallbackFailureExpected = true;
        await using MicrosoftAccountAuthenticator service = Create(fixture);

        // Act
        Outcome<AccessToken> result = await service.AcquireAsync(true, CancellationToken.None);

        // Assert
        Assert.That(result.IsSuccess, Is.False);
        Assert.That(fixture.Exchanges, Is.Zero);
        Assert.That(fixture.Shared.Stored, Is.Empty);
    }

    [Test]
    public async Task AcquireAsync_WhenRefreshGrantRejected_RetainsCredentialsForReconnectDecision()
    {
        // Arrange
        await using MicrosoftAuthenticationFixture fixture = new MicrosoftAuthenticationFixture { ExpiresIn = 30 };
        await using MicrosoftAccountAuthenticator service = Create(fixture);
        await service.AcquireAsync(true, CancellationToken.None);
        byte[] before = fixture.Shared.Stored.Single().Value.ToArray();
        fixture.Error = "invalid_grant";

        // Act
        Outcome<AccessToken> result = await service.AcquireAsync(false, CancellationToken.None);

        // Assert
        Assert.That(result.Failure, Is.EqualTo(Failure.NeedsAuthentication));
        Assert.That(fixture.Shared.Stored.Single().Value, Is.EqualTo(before));
    }

    private static MicrosoftAccountAuthenticator Create(MicrosoftAuthenticationFixture fixture)
    {
        return new MicrosoftAccountAuthenticator(new Postfold.Core.Models.Accounts.AccountId(1), MicrosoftAuthenticationFixture.ClientId,
            fixture.Shared, fixture.Shared, TimeProvider.System, TimeSpan.FromSeconds(15))
        {
            HttpFactory = fixture
        };
    }
}
