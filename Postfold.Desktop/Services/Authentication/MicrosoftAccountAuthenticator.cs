using Microsoft.Identity.Client;
using Postfold.Core.Enums;
using Postfold.Core.Interfaces;
using Postfold.Core.Models;
using Postfold.Core.Models.Accounts;
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Desktop.Services.Authentication;

/// <summary>Account-scoped MSAL public client. SDK cache and refresh tokens never enter password storage.</summary>
public sealed class MicrosoftAccountAuthenticator : IAccountAuthenticator, IAsyncDisposable
{
    private static readonly string[] Scopes =
    [
        "https://outlook.office.com/IMAP.AccessAsUser.All",
        "https://outlook.office.com/SMTP.Send"
    ];
    private readonly Postfold.Core.Models.Accounts.AccountId _account;
    private readonly string _clientId;
    private readonly string _key;
    private readonly IProtectedTokenStore _store;
    private readonly ISystemBrowser _browser;
    private readonly TimeProvider _time;
    private readonly TimeSpan _timeout;
    private readonly CancellationTokenSource _shutdown = new CancellationTokenSource();
    private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
    private string? _expectedSubject;
    private string? _expectedTenant;

    /// <summary>Pass retained home identity and tenant for established accounts, including after credential deletion.</summary>
    public MicrosoftAccountAuthenticator(
        Postfold.Core.Models.Accounts.AccountId account,
        string clientId,
        IProtectedTokenStore store,
        ISystemBrowser browser,
        TimeProvider time,
        TimeSpan timeout,
        string? expectedSubject = null,
        string? expectedTenant = null
    )
    {
        if (account.Value <= 0 || !Guid.TryParse(clientId, out _) || timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(10) ||
            (expectedSubject is null) != (expectedTenant is null) || expectedTenant is not null && !Guid.TryParse(expectedTenant, out _))
        {
            throw new ArgumentException("A registered public client and positive account/timeout are required.");
        }

        _account = account;
        _clientId = clientId;
        _key = "microsoft:" + clientId + ":" + account.Value.ToString(CultureInfo.InvariantCulture);
        _store = store;
        _browser = browser;
        _time = time;
        _timeout = timeout;
        _expectedSubject = expectedSubject;
        _expectedTenant = expectedTenant;
    }

    internal IMsalHttpClientFactory? HttpFactory { get; init; }

    public async Task<Outcome<AccessToken>> AcquireAsync(bool interactive, CancellationToken cancellationToken)
    {
        using CancellationTokenSource expiry = new CancellationTokenSource(_timeout, _time);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token, expiry.Token);
        await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        byte[] cacheBytes = [];
        try
        {
            Outcome<byte[]> stored = await _store.ReadAsync(_key, linked.Token).ConfigureAwait(false);
            if (!stored.IsSuccess && stored.Failure != Failure.NotFound)
            {
                return Outcome<AccessToken>.Fail(stored.Failure);
            }

            Cache? previous = null;
            PublicClientApplicationBuilder builder = PublicClientApplicationBuilder.Create(_clientId)
                .WithAuthority(AzureCloudInstance.AzurePublic, AadAuthorityAudience.AzureAdAndPersonalMicrosoftAccount)
                .WithRedirectUri("http://localhost")
                .WithLegacyCacheCompatibility(false);
            if (HttpFactory is not null)
            {
                builder = builder.WithHttpClientFactory(HttpFactory);
            }

            IPublicClientApplication application = builder.Build();
            if (stored.IsSuccess)
            {
                try
                {
                    previous = JsonSerializer.Deserialize<Cache>(stored.Value!);
                    if (previous is null || previous.Account != _account.Value || previous.Client != _clientId ||
                        string.IsNullOrWhiteSpace(previous.HomeId) || !Guid.TryParse(previous.Tenant, out _))
                    {
                        return Outcome<AccessToken>.Fail(Failure.Corrupt);
                    }

                    cacheBytes = previous.Bytes;
                    if (_expectedSubject is not null && _expectedSubject != previous.HomeId ||
                        _expectedTenant is not null && _expectedTenant != previous.Tenant)
                    {
                        return Outcome<AccessToken>.Fail(Failure.NeedsReview);
                    }

                    _expectedSubject ??= previous.HomeId;
                    _expectedTenant ??= previous.Tenant;
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(stored.Value!);
                }
            }

            application.UserTokenCache.SetBeforeAccess(notification => notification.TokenCache.DeserializeMsalV3(cacheBytes, true));
            application.UserTokenCache.SetAfterAccess(notification =>
            {
                if (notification.HasStateChanged)
                {
                    CryptographicOperations.ZeroMemory(cacheBytes);
                    cacheBytes = notification.TokenCache.SerializeMsalV3();
                }
            });

            AuthenticationResult result;
            if (interactive)
            {
                result = await application.AcquireTokenInteractive(Scopes)
                    .WithTenantId(_expectedTenant ?? "common")
                    .WithUseEmbeddedWebView(false)
                    .WithPrompt(Prompt.SelectAccount)
                    .WithSystemWebViewOptions(new SystemWebViewOptions
                    {
                        HtmlMessageSuccess = "<html><body>Return to Postfold. Close this tab.</body></html>",
                        HtmlMessageError = "<html><body>Return to Postfold. Close this tab.</body></html>",
                        OpenBrowserAsync = async url =>
                        {
                            Failure opened = await _browser.OpenAsync(url, linked.Token).ConfigureAwait(false);
                            if (opened != Failure.None)
                            {
                                throw new MsalClientException("browser_unavailable", "System browser unavailable.");
                            }
                        }
                    })
                    .ExecuteAsync(linked.Token).ConfigureAwait(false);
            }
            else
            {
                if (previous is null)
                {
                    return Outcome<AccessToken>.Fail(Failure.NeedsAuthentication);
                }

                IAccount? account = await application.GetAccountAsync(previous.HomeId).ConfigureAwait(false);
                if (account is null)
                {
                    return Outcome<AccessToken>.Fail(Failure.NeedsAuthentication);
                }

                result = await application.AcquireTokenSilent(Scopes, account)
                    .WithTenantId(_expectedTenant!)
                    .ExecuteAsync(linked.Token).ConfigureAwait(false);
            }

            if (!Scopes.All(scope => result.Scopes.Contains(scope, StringComparer.OrdinalIgnoreCase)) ||
                string.IsNullOrWhiteSpace(result.AccessToken) || result.Account is null ||
                !Guid.TryParse(result.TenantId, out _) || result.ExpiresOn <= _time.GetUtcNow())
            {
                return Outcome<AccessToken>.Fail(Failure.NeedsAuthentication);
            }

            string home = result.Account.HomeAccountId.Identifier;
            if (_expectedSubject is not null && _expectedSubject != home ||
                _expectedTenant is not null && _expectedTenant != result.TenantId ||
                previous is not null && (previous.HomeId != home || previous.Tenant != result.TenantId))
            {
                return Outcome<AccessToken>.Fail(Failure.NeedsReview);
            }

            Cache next = new Cache(_account.Value, _clientId, home, result.TenantId, cacheBytes);
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(next);
            Outcome<bool> saved;
            try
            {
                saved = await _store.WriteAsync(_key, bytes, linked.Token).ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
                CryptographicOperations.ZeroMemory(next.Bytes);
            }

            if (saved.IsSuccess)
            {
                _expectedSubject = home;
                _expectedTenant = result.TenantId;
            }

            return saved.IsSuccess ? Outcome<AccessToken>.Success(new AccessToken(
                _account,
                home,
                result.AccessToken,
                result.ExpiresOn,
                result.TenantId,
                "https://outlook.office.com"
            )) :
                Outcome<AccessToken>.Fail(Failure.CredentialStorageUnavailable);
        }
        catch (MsalException exception)
        {
            linked.Token.ThrowIfCancellationRequested();
            return Outcome<AccessToken>.Fail(Classify(exception));
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or IOException)
        {
            return Outcome<AccessToken>.Fail(Failure.Corrupt);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(cacheBytes);
            _gate.Release();
        }
    }

    /// <summary>Removes this account's entire local cache; server-side revocation requires Microsoft's account/admin controls.</summary>
    public async Task<Outcome<bool>> DeleteCredentialsAsync(CancellationToken cancellationToken)
    {
        using CancellationTokenSource expiry = new CancellationTokenSource(_timeout, _time);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token, expiry.Token);
        cancellationToken = linked.Token;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await _store.DeleteAsync(_key, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        await _gate.WaitAsync().ConfigureAwait(false);
        _gate.Release();
    }

    /// <summary>Exposes only safe classifications. Never surface MSAL messages, claims, response bodies or inner exceptions.</summary>
    public static Failure Classify(MsalException exception)
    {
        if (exception.ErrorCode is "access_denied" or "unauthorized_client" or "invalid_client" or "invalid_scope" ||
            exception is MsalUiRequiredException required && required.Classification == UiRequiredExceptionClassification.ConsentRequired)
        {
            return Failure.PolicyBlocked;
        }

        if (exception is MsalUiRequiredException || exception.ErrorCode is "invalid_grant" or "authentication_canceled")
        {
            return Failure.NeedsAuthentication;
        }

        return exception is MsalServiceException service && (service.StatusCode >= 500 || service.StatusCode == 429) ?
            Failure.Transient : Failure.Unavailable;
    }

    private sealed record Cache(long Account, string Client, string HomeId, string Tenant, byte[] Bytes)
    {
        public override string ToString()
        {
            return "[MSAL cache]";
        }
    }
}
