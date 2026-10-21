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

/// <summary>One bounded, redirect-free Gmail HTTP owner. No request is retried here, including side effects.</summary>
internal sealed class GmailApiClient : IDisposable
{
    private readonly AccountId _account;
    private readonly GoogleAccountBinding _binding;
    private readonly IAccountAuthenticator? _authentication;
    private readonly HttpClient _http;
    private readonly TimeProvider _time;
    private readonly bool _ownsHttp;
    private DateTimeOffset _notBefore;

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
        bool started = false;
        bool effect = method != HttpMethod.Get;
        try
        {
            if (!_binding.IsValid || _authentication is null)
            {
                throw new RequestFailure(Failure.NeedsAuthentication, false);
            }
            if (_time.GetUtcNow() < _notBefore)
            {
                throw new RequestFailure(Failure.Transient, false);
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
            started = effect;
            using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (response.Headers.RetryAfter is RetryConditionHeaderValue retry)
            {
                DateTimeOffset until = retry.Date ?? _time.GetUtcNow().Add(retry.Delta ?? TimeSpan.Zero);
                // A cooldown gates subsequent scheduled reads without hiding an active operation in a sleep.
                if (until > _notBefore)
                {
                    _notBefore = until;
                }
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
                        }
                    }
                }
                throw new RequestFailure(failure, started && ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests), started);
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

    internal sealed class RequestFailure(Failure failure, bool uncertain, bool attempted = false) : Exception("Gmail request failed.")
    {
        internal Failure Failure { get; } = failure;
        internal bool Uncertain { get; } = uncertain;
        internal bool Attempted { get; } = attempted || uncertain;
    }
}
