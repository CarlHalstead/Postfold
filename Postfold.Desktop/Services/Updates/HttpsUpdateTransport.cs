using Postfold.Core.Enums;
using Postfold.Core.Models;
using Postfold.Core.Models.Updates;
using Postfold.Core.Policies.Updates;
using Postfold.Desktop.Interfaces;
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Desktop.Services.Updates;

/// <summary>HTTPS-only GitHub release transport. Discovery is a hint; signatures establish artifact identity.</summary>
internal sealed class HttpsUpdateTransport : IUpdateTransport, IDisposable
{
    private readonly Uri _release;
    private readonly HttpClient _client;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _requests = new SemaphoreSlim(1, 1);
    private int _failures;

    internal HttpsUpdateTransport(Uri release, TimeProvider time, HttpMessageHandler? fixtureHandler = null)
    {
        if (!Allowed(release) || release.Host != "github.com" ||
            !release.AbsolutePath.StartsWith("/CarlHalstead/Postfold/releases/download/", StringComparison.Ordinal) ||
            !release.AbsolutePath.EndsWith('/') || release.Query.Length != 0)
        {
            throw new ArgumentException("An immutable Postfold GitHub release location is required.", nameof(release));
        }
        _release = release;
        _time = time;
        _client = new HttpClient(fixtureHandler ?? new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None
        })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("Postfold/1.0");
    }

    internal DateTimeOffset RetryAt { get; private set; }
    internal bool RateLimited { get; private set; }

    public async Task<Outcome<byte[]>> FetchAsync(string name, int maximumBytes, CancellationToken cancellationToken)
    {
        if (maximumBytes is <= 0 or > 262144)
        {
            return Outcome<byte[]>.Fail(Failure.Validation);
        }
        using MemoryStream destination = new MemoryStream();
        Failure failure = await TransferAsync(name, maximumBytes, null, destination, cancellationToken).ConfigureAwait(false);
        return failure == Failure.None ? Outcome<byte[]>.Success(destination.ToArray()) : Outcome<byte[]>.Fail(failure);
    }

    public Task<Failure> DownloadAsync(UpdateArtifact artifact, Stream destination, CancellationToken cancellationToken)
    {
        return TransferAsync(artifact.Name, artifact.Length, artifact, destination, cancellationToken);
    }

    public void Dispose()
    {
        _client.Dispose();
    }

    private static bool Allowed(Uri uri)
    {
        return uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps && uri.Port == 443 &&
            uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 &&
            (uri.Host == "github.com" && uri.AbsolutePath.StartsWith("/CarlHalstead/Postfold/releases/download/", StringComparison.Ordinal) ||
                uri.Host == "release-assets.githubusercontent.com");
    }

    private async Task<Failure> TransferAsync(
        string name,
        long maximumBytes,
        UpdateArtifact? artifact,
        Stream destination,
        CancellationToken cancellationToken
    )
    {
        if (name.Length is <= 0 or > 128 || name.Contains("..", StringComparison.Ordinal) ||
            !System.Text.RegularExpressions.Regex.IsMatch(name, @"\A[A-Za-z0-9][A-Za-z0-9._-]*\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant) ||
            maximumBytes is <= 0 or > 2147483648)
        {
            return Failure.Validation;
        }
        await _requests.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_time.GetUtcNow() < RetryAt)
            {
                return Failure.Transient;
            }
            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(artifact is null ? TimeSpan.FromSeconds(30) : TimeSpan.FromMinutes(10));
            Uri uri = new Uri(_release, name);
            for (int redirect = 0; redirect <= 4; redirect++)
            {
                using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, uri);
                using HttpResponseMessage response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
                if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
                {
                    Uri? location = response.Headers.Location;
                    if (redirect == 4 || location is null)
                    {
                        return Failure.Validation;
                    }
                    uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                    if (!Allowed(uri))
                    {
                        return Failure.Validation;
                    }
                    continue;
                }
                if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable or HttpStatusCode.Forbidden)
                {
                    RateLimited = response.StatusCode == HttpStatusCode.TooManyRequests ||
                        response.StatusCode == HttpStatusCode.Forbidden && response.Headers.TryGetValues("X-RateLimit-Remaining", out System.Collections.Generic.IEnumerable<string>? values) && values.Contains("0");
                    if (response.StatusCode == HttpStatusCode.Forbidden && !RateLimited)
                    {
                        return Failure.PolicyBlocked;
                    }
                    TimeSpan retry = response.Headers.RetryAfter?.Delta ??
                        (response.Headers.RetryAfter?.Date - _time.GetUtcNow()) ?? TimeSpan.FromSeconds(30);
                    RetryAt = _time.GetUtcNow() + TimeSpan.FromSeconds(Math.Clamp(retry.TotalSeconds, 1, 3600));
                    return Failure.Transient;
                }
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    return Failure.NotFound;
                }
                if ((int)response.StatusCode >= 500)
                {
                    Backoff();
                    return Failure.Transient;
                }
                if (!response.IsSuccessStatusCode || response.Content.Headers.ContentEncoding.Count != 0 ||
                    response.Content.Headers.ContentLength > maximumBytes)
                {
                    return Failure.Validation;
                }
                using Stream source = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
                Failure result;
                if (artifact is not null)
                {
                    result = await UpdateArtifacts.CopyVerifiedAsync(source, destination, artifact, deadline.Token).ConfigureAwait(false);
                }
                else
                {
                    byte[] buffer = new byte[16384];
                    long length = 0;
                    int read;
                    while ((read = await source.ReadAsync(buffer, deadline.Token).ConfigureAwait(false)) != 0)
                    {
                        length += read;
                        if (length > maximumBytes)
                        {
                            return Failure.Validation;
                        }
                        await destination.WriteAsync(buffer.AsMemory(0, read), deadline.Token).ConfigureAwait(false);
                    }
                    result = Failure.None;
                }
                if (result == Failure.None)
                {
                    _failures = 0;
                    RateLimited = false;
                    RetryAt = default;
                }
                return result;
            }
            return Failure.Validation;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Backoff();
            return Failure.Unavailable;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            Backoff();
            return exception is HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError } ? Failure.TlsRejected : Failure.Unavailable;
        }
        finally
        {
            _requests.Release();
        }
    }

    private void Backoff()
    {
        _failures = Math.Min(_failures + 1, 9);
        RetryAt = _time.GetUtcNow() + TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, _failures)));
        RateLimited = false;
    }
}
