using Postfold.Core.Enums;
using Postfold.Core.Interfaces;
using Postfold.Core.Models;
using Postfold.Core.Models.Accounts;
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Protocols.Services;

/// <summary>One bounded, redirect-free Gmail HTTP owner. Only quota-rejected GETs may be retried.</summary>
internal sealed class GmailApiClient : IDisposable
{
    private readonly AccountId _account;
    private readonly GoogleAccountBinding _binding;
    private readonly IAccountAuthenticator? _authentication;
    private readonly HttpClient _http;
    private readonly TimeProvider _time;
    private readonly bool _ownsHttp;
    private readonly SemaphoreSlim _requests = new SemaphoreSlim(1, 1);
    private readonly object _cooldownGate = new object();
    private DateTimeOffset _notBefore;
    private DateTimeOffset _nextRequest;

    internal GmailApiClient(
        AccountId account,
        GoogleAccountBinding binding,
        IAccountAuthenticator? authentication,
        HttpClient http,
        TimeProvider time,
        bool ownsHttp
    )
    {
        _account = account;
        _binding = binding;
        _authentication = authentication;
        _http = http;
        _time = time;
        _ownsHttp = ownsHttp;
    }

    public void Dispose()
    {
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }

    internal async Task<JsonDocument> RequestAsync(
        HttpMethod method,
        string path,
        object? payload,
        long maximumBytes,
        CancellationToken cancellationToken
    )
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return await SendAsync(method, path, payload, maximumBytes, cancellationToken).ConfigureAwait(false);
            }
            catch (RequestFailure failure) when (method == HttpMethod.Get && attempt == 0 && failure.RateLimited &&
                failure.RetryAt - _time.GetUtcNow() <= TimeSpan.FromMinutes(1))
            {
                TimeSpan delay = failure.RetryAt - _time.GetUtcNow();
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, _time, cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    private async Task<JsonDocument> SendAsync(
        HttpMethod method,
        string path,
        object? payload,
        long maximumBytes,
        CancellationToken cancellationToken
    )
    {
        bool started = false;
        bool effect = method != HttpMethod.Get;
        try
        {
            if (!_binding.IsValid || _authentication is null)
            {
                throw new RequestFailure(Failure.NeedsAuthentication, false);
            }

            Outcome<AccessToken> acquired = await _authentication.AcquireAsync(false, cancellationToken).ConfigureAwait(false);
            if (!acquired.IsSuccess)
            {
                throw new RequestFailure(acquired.Failure, false);
            }
            AccessToken token = acquired.Value!;
            if (token.Account != _account || token.ExpiresAt <= _time.GetUtcNow() ||
                !string.Equals(token.Subject, _binding.Address, StringComparison.OrdinalIgnoreCase) || token.Resource is not null)
            {
                throw new RequestFailure(Failure.NeedsReview, false);
            }

            using HttpRequestMessage request = new HttpRequestMessage(method, "https://gmail.googleapis.com/gmail/v1/users/me/" + path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Secret);
            if (payload is not null)
            {
                request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            }
            cancellationToken.ThrowIfCancellationRequested();
            await ReserveQuotaAsync(method, path, cancellationToken).ConfigureAwait(false);
            started = effect;
            using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (response.Headers.RetryAfter is RetryConditionHeaderValue retry)
            {
                DateTimeOffset until = retry.Date ?? _time.GetUtcNow().Add(retry.Delta ?? TimeSpan.Zero);
                // Respect provider cooldowns for reads and subsequent scheduled side effects.
                ExtendCooldown(until);
            }
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                throw new RequestFailure(Failure.PolicyBlocked, started);
            }

            using MemoryStream bytes = new MemoryStream();
            await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using BoundedStream bounded = new BoundedStream(source, response.IsSuccessStatusCode ? maximumBytes : 65536);
            await bounded.CopyToAsync(bytes, cancellationToken).ConfigureAwait(false);
            JsonDocument parsed = JsonDocument.Parse(bytes.GetBuffer().AsMemory(0, checked((int)bytes.Length)), new JsonDocumentOptions
            {
                MaxDepth = 32
            });
            if (parsed.RootElement.ValueKind != JsonValueKind.Object)
            {
                parsed.Dispose();
                throw new RequestFailure(Failure.Corrupt, started);
            }
            if (!response.IsSuccessStatusCode)
            {
                using JsonDocument rejected = parsed;
                bool rateLimited = response.StatusCode == HttpStatusCode.TooManyRequests;
                Failure failure = response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => Failure.NeedsAuthentication,
                    HttpStatusCode.Forbidden => Failure.PolicyBlocked,
                    HttpStatusCode.NotFound => Failure.NotFound,
                    HttpStatusCode.TooManyRequests => Failure.Transient,
                    _ => (int)response.StatusCode >= 500 ? Failure.Transient : Failure.Validation
                };
                if (response.StatusCode == HttpStatusCode.Forbidden && parsed.RootElement.TryGetProperty("error", out JsonElement error) &&
                    error.TryGetProperty("errors", out JsonElement errors) && errors.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement item in errors.EnumerateArray())
                    {
                        if (item.TryGetProperty("reason", out JsonElement reason) &&
                            reason.GetString() is "rateLimitExceeded" or "userRateLimitExceeded" or "dailyLimitExceeded" or "quotaExceeded")
                        {
                            failure = Failure.Transient;
                            rateLimited = reason.GetString() is "rateLimitExceeded" or "userRateLimitExceeded";
                        }
                    }
                }
                DateTimeOffset retryAt;
                lock (_cooldownGate)
                {
                    if (rateLimited && _notBefore <= _time.GetUtcNow())
                    {
                        _notBefore = _time.GetUtcNow().AddMinutes(1);
                    }
                    retryAt = _notBefore;
                }
                throw new RequestFailure(failure, started && ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests), started, rateLimited, retryAt);
            }

            return parsed;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException or InvalidDataException or ArgumentException or OverflowException)
        {
            throw new RequestFailure(Failure.Corrupt, started);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            throw new RequestFailure(Failure.Transient, started);
        }
        catch (OperationCanceledException) when (started)
        {
            throw new RequestFailure(Failure.NeedsReview, true);
        }
    }

    private async Task ReserveQuotaAsync(HttpMethod method, string path, CancellationToken cancellationToken)
    {
        // Serialise quota reservations, not HTTP responses or whole sync pages. The adapter
        // bounds this to one serial operation plus one selected-content read. Both share the
        // 4,800-unit/minute budget and cancel/drain before HTTP disposal on account shutdown.
        await _requests.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            TimeSpan pacing = _nextRequest - _time.GetUtcNow();
            if (pacing > TimeSpan.Zero)
            {
                await Task.Delay(pacing, _time, cancellationToken).ConfigureAwait(false);
            }
            lock (_cooldownGate)
            {
                if (_time.GetUtcNow() < _notBefore)
                {
                    throw new RequestFailure(Failure.Transient, false, rateLimited: true, retryAt: _notBefore);
                }
            }
            _nextRequest = _time.GetUtcNow().AddSeconds(QuotaCost(method, path) / 80d);
        }
        finally
        {
            _requests.Release();
        }
    }

    private void ExtendCooldown(DateTimeOffset until)
    {
        lock (_cooldownGate)
        {
            if (until > _notBefore)
            {
                _notBefore = until;
            }
        }
    }

    private static int QuotaCost(HttpMethod method, string path)
    {
        if (method != HttpMethod.Get)
        {
            return path.EndsWith("/send", StringComparison.Ordinal) ? 100 :
                path.EndsWith("/modify", StringComparison.Ordinal) ? 5 : 15;
        }
        if (path.StartsWith("messages/", StringComparison.Ordinal) || path.StartsWith("drafts/", StringComparison.Ordinal))
        {
            return path.Contains("format=metadata", StringComparison.Ordinal) ||
                path.Contains("format=minimal", StringComparison.Ordinal) ||
                path.Contains("/attachments/", StringComparison.Ordinal) ? 20 : 60;
        }
        return path.StartsWith("history?", StringComparison.Ordinal) ? 2 :
            path.StartsWith("messages?", StringComparison.Ordinal) ? 5 : 1;
    }

    internal sealed class RequestFailure(
        Failure failure,
        bool uncertain,
        bool attempted = false,
        bool rateLimited = false,
        DateTimeOffset retryAt = default
    ) : Exception("Gmail request failed.")
    {
        internal Failure Failure { get; } = failure;
        internal bool Uncertain { get; } = uncertain;
        internal bool Attempted { get; } = attempted || uncertain;
        internal bool RateLimited { get; } = rateLimited;
        internal DateTimeOffset RetryAt { get; } = retryAt;
    }
}
