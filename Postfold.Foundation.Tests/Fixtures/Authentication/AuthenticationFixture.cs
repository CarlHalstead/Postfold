using Microsoft.Identity.Client;
using Postfold.Core.Enums;
using Postfold.Core.Interfaces;
using Postfold.Core.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Foundation.Tests.Fixtures.Authentication;

/// <summary>All provider HTTP is intercepted; the sole socket is the real ephemeral loopback callback.</summary>
internal sealed class AuthenticationFixture : HttpMessageHandler, IProtectedTokenStore, ISystemBrowser, IMsalHttpClientFactory, IAsyncDisposable
{
    internal AuthenticationFixture()
    {
        Http = new HttpClient(this, false);
    }

    internal HttpClient Http { get; }
    internal Dictionary<string, byte[]> Stored { get; } = [];
    internal List<string> TokenRequests { get; } = [];
    internal string TokenJson { get; set; } = "{\"access_token\":\"synthetic-access\",\"refresh_token\":\"synthetic-refresh\",\"token_type\":\"Bearer\",\"expires_in\":3600,\"scope\":\"https://www.googleapis.com/auth/gmail.modify\"}";
    internal string Subject { get; set; } = "synthetic@example.test";
    internal string HistoryId { get; set; } = "100";
    internal Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? ProviderRequest { get; set; }
    internal Func<HttpRequestMessage, CancellationToken, Task>? BeforeRequest { get; set; }
    internal string? CodeChallengeOverride { get; set; }
    internal string? RequiredGoogleClientSecret { get; set; }
    internal string? CallbackQuery { get; set; }
    internal string? CallbackHost { get; set; }
    internal string CallbackPath { get; set; } = "/";
    internal Failure StoreFailure { get; set; }
    internal Failure WriteFailure { get; set; }
    internal Failure DeleteFailure { get; set; }
    internal Failure BrowserFailure { get; set; }
    internal HttpStatusCode TokenStatus { get; set; } = HttpStatusCode.OK;
    internal bool SendCallback { get; set; } = true;
    internal bool CallbackFailureExpected { get; set; }
    internal TaskCompletionSource<Uri> Opened { get; } = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task<string>? Callback { get; private set; }
    internal Uri? Authorization { get; private set; }

    public HttpClient GetHttpClient()
    {
        return Http;
    }

    public Task<Failure> OpenAsync(Uri uri, CancellationToken cancellationToken)
    {
        Authorization = uri;
        Opened.TrySetResult(uri);
        if (BrowserFailure != Failure.None || !SendCallback)
        {
            return Task.FromResult(BrowserFailure);
        }

        Dictionary<string, string> query = Query(uri);
        string callbackQuery = CallbackQuery ?? "code=synthetic-code&state=" + Uri.EscapeDataString(query["state"]);
        callbackQuery = callbackQuery.Replace("{state}", Uri.EscapeDataString(query["state"]), StringComparison.Ordinal);
        Callback = CallbackAsync(new Uri(query["redirect_uri"]), callbackQuery,
            query.GetValueOrDefault("response_mode") == "form_post", cancellationToken);
        return Task.FromResult(Failure.None);
    }

    public Task<Outcome<byte[]>> ReadAsync(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(StoreFailure != Failure.None ? Outcome<byte[]>.Fail(StoreFailure) :
            Stored.TryGetValue(key, out byte[]? value) ? Outcome<byte[]>.Success(value.ToArray()) : Outcome<byte[]>.Fail(Failure.NotFound));
    }

    public Task<Outcome<bool>> WriteAsync(string key, byte[] bytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (StoreFailure != Failure.None || WriteFailure != Failure.None)
        {
            return Task.FromResult(Outcome<bool>.Fail(StoreFailure != Failure.None ? StoreFailure : WriteFailure));
        }

        Stored[key] = bytes.ToArray();
        return Task.FromResult(Outcome<bool>.Success(true));
    }

    public Task<Outcome<bool>> DeleteAsync(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (DeleteFailure != Failure.None)
        {
            return Task.FromResult(Outcome<bool>.Fail(DeleteFailure));
        }
        Stored.Remove(key);
        return Task.FromResult(Outcome<bool>.Success(true));
    }

    public async ValueTask DisposeAsync()
    {
        if (Callback is not null)
        {
            await Callback.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Http.Dispose();
        Dispose();
    }

    internal static Dictionary<string, string> Query(Uri uri)
    {
        return uri.Query.TrimStart('?').Split('&').Select(pair => pair.Split('=', 2))
            .ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => Uri.UnescapeDataString(pair[1].Replace('+', ' ')));
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (BeforeRequest is not null)
        {
            await BeforeRequest(request, cancellationToken);
        }
        string uri = request.RequestUri!.AbsoluteUri;
        string json;
        HttpStatusCode status = HttpStatusCode.OK;
        if (uri == "https://oauth2.googleapis.com/token")
        {
            string body = await request.Content!.ReadAsStringAsync(cancellationToken);
            TokenRequests.Add(body);
            json = TokenJson;
            status = TokenStatus;
            Dictionary<string, string> fields = Query(new Uri("https://fixture.test/?" + body));
            if (fields.GetValueOrDefault("grant_type") == "authorization_code")
            {
                string challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(fields["code_verifier"])))
                    .TrimEnd('=').Replace('+', '-').Replace('/', '_');
                string expected = CodeChallengeOverride ?? Query(Authorization!)["code_challenge"];
                if (challenge != expected || fields["code"] != "synthetic-code")
                {
                    json = "{\"error\":\"invalid_grant\"}";
                    status = HttpStatusCode.BadRequest;
                }
            }
            if (RequiredGoogleClientSecret is not null && fields.GetValueOrDefault("client_secret") != RequiredGoogleClientSecret)
            {
                json = "{\"error\":\"invalid_request\",\"error_description\":\"client_secret is missing.\"}";
                status = HttpStatusCode.BadRequest;
            }
        }
        else if (uri == "https://gmail.googleapis.com/gmail/v1/users/me/profile")
        {
            json = "{\"emailAddress\":\"" + Subject + "\",\"historyId\":\"" + HistoryId + "\"}";
        }
        else if (uri == "https://oauth2.googleapis.com/revoke")
        {
            json = "{}";
        }
        else if (ProviderRequest is not null)
        {
            return await ProviderRequest(request, cancellationToken);
        }
        else
        {
            throw new InvalidOperationException("Unexpected synthetic provider endpoint.");
        }

        return new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private async Task<string> CallbackAsync(Uri redirect, string query, bool formPost, CancellationToken cancellationToken)
    {
        using TcpClient client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, redirect.Port, cancellationToken);
        using NetworkStream stream = client.GetStream();
        string headers = " HTTP/1.1\r\nHost: " + (CallbackHost ?? redirect.Authority) + "\r\n";
        byte[] request = Encoding.ASCII.GetBytes(formPost ? "POST " + CallbackPath + headers +
            "Content-Type: application/x-www-form-urlencoded\r\nContent-Length: " + query.Length + "\r\n\r\n" + query :
            "GET " + CallbackPath + "?" + query + headers + "\r\n");
        await stream.WriteAsync(request, cancellationToken);
        byte[] response = new byte[4096];
        try
        {
            int read = await stream.ReadAsync(response, cancellationToken);
            return Encoding.ASCII.GetString(response, 0, read);
        }
        catch (System.IO.IOException) when (CallbackFailureExpected)
        {
            return "[rejected callback connection]";
        }
    }
}
