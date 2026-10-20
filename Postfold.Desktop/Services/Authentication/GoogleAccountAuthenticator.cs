using Postfold.Core.Enums;
using Postfold.Core.Interfaces;
using Postfold.Core.Models;
using Postfold.Core.Models.Accounts;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Desktop.Services.Authentication;

/// <summary>Google-only native PKCE and refresh. Own once per account; dispose before profile shutdown.</summary>
public sealed class GoogleAccountAuthenticator : IAccountAuthenticator, IAuthorizationRevoker, IAsyncDisposable
{
    public const string Scope = "https://www.googleapis.com/auth/gmail.modify";
    private readonly AccountId _account;
    private readonly string _clientId;
    private readonly string _key;
    private readonly IProtectedTokenStore _store;
    private readonly ISystemBrowser _browser;
    private readonly HttpClient _http;
    private readonly TimeProvider _time;
    private readonly TimeSpan _timeout;
    private readonly CancellationTokenSource _shutdown = new CancellationTokenSource();
    private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
    private string? _expectedSubject;
    private readonly bool _ownsHttp;

    /// <summary>Pass the retained Gmail profile address for an established account, including after credential deletion.</summary>
    public GoogleAccountAuthenticator(
        AccountId account,
        string clientId,
        IProtectedTokenStore store,
        ISystemBrowser browser,
        TimeProvider time,
        TimeSpan timeout,
        string? expectedSubject = null
    ) : this(
        account,
        clientId,
        store,
        browser,
        new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false
        }),
        time,
        timeout,
        expectedSubject
    )
    {
        _ownsHttp = true;
    }

    internal GoogleAccountAuthenticator(
        AccountId account,
        string clientId,
        IProtectedTokenStore store,
        ISystemBrowser browser,
        HttpClient http,
        TimeProvider time,
        TimeSpan timeout,
        string? expectedSubject = null
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        if (account.Value <= 0 || timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(10))
        {
            throw new ArgumentOutOfRangeException(nameof(account));
        }

        _account = account;
        _clientId = clientId;
        _key = "google:" + clientId + ":" + account.Value.ToString(CultureInfo.InvariantCulture);
        _store = store;
        _browser = browser;
        _http = http;
        _time = time;
        _timeout = timeout;
        _expectedSubject = expectedSubject;
    }

    public async Task<Outcome<AccessToken>> AcquireAsync(bool interactive, CancellationToken cancellationToken)
    {
        using CancellationTokenSource expiry = new CancellationTokenSource(_timeout, _time);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token, expiry.Token);
        await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            Outcome<Grant> loaded = await LoadAsync(linked.Token).ConfigureAwait(false);
            if (!loaded.IsSuccess && loaded.Failure != Failure.NotFound)
            {
                return Outcome<AccessToken>.Fail(loaded.Failure);
            }

            Grant? previous = loaded.Value;
            _expectedSubject ??= previous?.Subject;
            if (!interactive && previous is null)
            {
                return Outcome<AccessToken>.Fail(Failure.NeedsAuthentication);
            }

            if (!interactive && previous!.ExpiresAt > _time.GetUtcNow().AddMinutes(2))
            {
                return Outcome<AccessToken>.Success(new AccessToken(_account, previous.Subject, previous.Access, previous.ExpiresAt));
            }

            Dictionary<string, string> fields = new Dictionary<string, string>
            {
                ["client_id"] = _clientId
            };
            if (interactive)
            {
                using GoogleAuthorizationAttempt attempt = new GoogleAuthorizationAttempt();
                Outcome<string> code = await attempt.ReceiveAsync(_browser, _clientId, linked.Token).ConfigureAwait(false);
                if (!code.IsSuccess)
                {
                    return Outcome<AccessToken>.Fail(code.Failure);
                }

                fields["grant_type"] = "authorization_code";
                fields["code"] = code.Value!;
                fields["code_verifier"] = attempt.Verifier;
                fields["redirect_uri"] = attempt.Redirect.AbsoluteUri;
            }
            else
            {
                fields["grant_type"] = "refresh_token";
                fields["refresh_token"] = previous!.Refresh;
            }

            using FormUrlEncodedContent content = new FormUrlEncodedContent(fields);
            using HttpRequestMessage exchange = new HttpRequestMessage(HttpMethod.Post, "https://oauth2.googleapis.com/token")
            {
                Content = content
            };
            using HttpResponseMessage response = await _http.SendAsync(exchange, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
            using JsonDocument json = await ReadJsonAsync(response, linked.Token).ConfigureAwait(false);
            JsonElement root = json.RootElement;
            if (!response.IsSuccessStatusCode)
            {
                string? error = root.TryGetProperty("error", out JsonElement value) ? value.GetString() : null;
                return Outcome<AccessToken>.Fail(error is "admin_policy_enforced" or "access_denied" ? Failure.PolicyBlocked :
                    error == "invalid_grant" ? Failure.NeedsAuthentication :
                    (int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests ? Failure.Transient : Failure.Validation);
            }

            string access = root.GetProperty("access_token").GetString()!;
            string? refresh = root.TryGetProperty("refresh_token", out JsonElement rotation) ? rotation.GetString() : previous?.Refresh;
            string? scopes = root.TryGetProperty("scope", out JsonElement granted) ? granted.GetString() : interactive ? null : Scope;
            if (root.GetProperty("token_type").GetString() != "Bearer" || scopes != Scope ||
                string.IsNullOrWhiteSpace(access) || string.IsNullOrWhiteSpace(refresh) ||
                !root.GetProperty("expires_in").TryGetInt32(out int seconds) || seconds <= 0 || seconds > 86400)
            {
                return Outcome<AccessToken>.Fail(Failure.NeedsAuthentication);
            }

            using HttpRequestMessage profile = new HttpRequestMessage(HttpMethod.Get, "https://gmail.googleapis.com/gmail/v1/users/me/profile");
            profile.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
            using HttpResponseMessage identity = await _http.SendAsync(profile, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
            if (!identity.IsSuccessStatusCode)
            {
                return Outcome<AccessToken>.Fail(identity.StatusCode == HttpStatusCode.Forbidden ? Failure.PolicyBlocked : Failure.NeedsAuthentication);
            }

            using JsonDocument mailbox = await ReadJsonAsync(identity, linked.Token).ConfigureAwait(false);
            string subject = mailbox.RootElement.GetProperty("emailAddress").GetString()!;
            if (string.IsNullOrWhiteSpace(subject) || _expectedSubject is not null &&
                !string.Equals(subject, _expectedSubject, StringComparison.OrdinalIgnoreCase) || previous is not null &&
                !string.Equals(subject, previous.Subject, StringComparison.OrdinalIgnoreCase))
            {
                return Outcome<AccessToken>.Fail(Failure.NeedsReview);
            }

            Grant next = new Grant(_account.Value, _clientId, subject, access, refresh, _time.GetUtcNow().AddSeconds(seconds));
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(next);
            Outcome<bool> stored;
            try
            {
                stored = await _store.WriteAsync(_key, bytes, linked.Token).ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }

            if (stored.IsSuccess)
            {
                _expectedSubject = subject;
            }

            return stored.IsSuccess ? Outcome<AccessToken>.Success(new AccessToken(_account, subject, access, next.ExpiresAt)) :
                Outcome<AccessToken>.Fail(Failure.CredentialStorageUnavailable);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            return Outcome<AccessToken>.Fail(Failure.Corrupt);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or System.Net.Sockets.SocketException)
        {
            return Outcome<AccessToken>.Fail(Failure.Transient);
        }
        finally
        {
            _gate.Release();
        }
    }

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

    public async Task<Outcome<bool>> RevokeAsync(CancellationToken cancellationToken)
    {
        using CancellationTokenSource expiry = new CancellationTokenSource(_timeout, _time);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token, expiry.Token);
        cancellationToken = linked.Token;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Outcome<Grant> loaded = await LoadAsync(cancellationToken).ConfigureAwait(false);
            if (!loaded.IsSuccess)
            {
                return Outcome<bool>.Fail(loaded.Failure);
            }

            using FormUrlEncodedContent content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["token"] = loaded.Value!.Refresh
            });
            using HttpRequestMessage revoke = new HttpRequestMessage(HttpMethod.Post, "https://oauth2.googleapis.com/revoke")
            {
                Content = content
            };
            using HttpResponseMessage response = await _http.SendAsync(revoke, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode ? await _store.DeleteAsync(_key, cancellationToken).ConfigureAwait(false) :
                Outcome<bool>.Fail(Failure.NeedsReview);
        }
        catch (HttpRequestException)
        {
            return Outcome<bool>.Fail(Failure.Transient);
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
        try
        {
            if (_ownsHttp)
            {
                _http.Dispose();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Outcome<Grant>> LoadAsync(CancellationToken cancellationToken)
    {
        Outcome<byte[]> stored = await _store.ReadAsync(_key, cancellationToken).ConfigureAwait(false);
        if (!stored.IsSuccess)
        {
            return Outcome<Grant>.Fail(stored.Failure);
        }

        try
        {
            Grant? grant = JsonSerializer.Deserialize<Grant>(stored.Value!);
            return grant is null || grant.Account != _account.Value || grant.Client != _clientId ||
                _expectedSubject is not null && !string.Equals(grant.Subject, _expectedSubject, StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(grant.Subject) || string.IsNullOrWhiteSpace(grant.Refresh) || string.IsNullOrWhiteSpace(grant.Access) ?
                Outcome<Grant>.Fail(Failure.Corrupt) : Outcome<Grant>.Success(grant);
        }
        catch (JsonException)
        {
            return Outcome<Grant>.Fail(Failure.Corrupt);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(stored.Value!);
        }
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await response.Content.LoadIntoBufferAsync(65536, cancellationToken).ConfigureAwait(false);
        return JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false));
    }

    private sealed record Grant(long Account, string Client, string Subject, string Access, string Refresh, DateTimeOffset ExpiresAt)
    {
        public override string ToString()
        {
            return "[Google grant]";
        }
    }
}
