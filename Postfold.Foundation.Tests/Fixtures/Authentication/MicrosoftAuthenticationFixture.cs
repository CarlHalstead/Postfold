using Microsoft.Identity.Client;
using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Foundation.Tests.Fixtures.Authentication;

/// <summary>Real MSAL against intercepted discovery/token HTTP; real MSAL loopback receiver, synthetic browser only.</summary>
internal sealed class MicrosoftAuthenticationFixture : HttpMessageHandler, IMsalHttpClientFactory, IAsyncDisposable
{
    internal const string ClientId = "11111111-1111-1111-1111-111111111111";
    internal const string Tenant = "22222222-2222-2222-2222-222222222222";
    internal const string User = "33333333-3333-3333-3333-333333333333";
    private readonly HttpClient _http;

    internal MicrosoftAuthenticationFixture()
    {
        _http = new HttpClient(this, false);
    }

    internal AuthenticationFixture Shared { get; } = new AuthenticationFixture();
    internal string ApplicationId { get; set; } = ClientId;
    internal int Exchanges { get; private set; }
    internal string? LastRequest { get; private set; }
    internal string? Error { get; set; }
    internal string GrantedScopes { get; set; } = "https://outlook.office.com/IMAP.AccessAsUser.All https://outlook.office.com/SMTP.Send";
    internal string HomeUser { get; set; } = User;
    internal int ExpiresIn { get; set; } = 3600;
    internal string AccessSecret { get; set; } = "synthetic-microsoft-access";
    internal string RefreshSecret { get; set; } = "synthetic-microsoft-refresh";
    internal string ResourceTenant { get; set; } = Tenant;
    internal bool BlockTokenRequests { get; set; }
    internal TaskCompletionSource TokenRequestBlocked { get; } = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource ReleaseTokenRequest { get; } = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource TokenRequestCancelled { get; } = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    public HttpClient GetHttpClient()
    {
        return _http;
    }

    public async ValueTask DisposeAsync()
    {
        await Shared.DisposeAsync();
        _http.Dispose();
        Dispose();
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Uri uri = request.RequestUri!;
        if (uri.Host != "login.microsoftonline.com")
        {
            throw new InvalidOperationException("Unexpected synthetic authority.");
        }

        string json;
        HttpStatusCode status = HttpStatusCode.OK;
        if (uri.AbsolutePath.Contains("discovery/instance", StringComparison.Ordinal))
        {
            json = "{\"tenant_discovery_endpoint\":\"https://login.microsoftonline.com/common/v2.0/.well-known/openid-configuration\",\"metadata\":[{\"preferred_network\":\"login.microsoftonline.com\",\"preferred_cache\":\"login.microsoftonline.com\",\"aliases\":[\"login.microsoftonline.com\"]}]}";
        }
        else if (uri.AbsolutePath.EndsWith("openid-configuration", StringComparison.Ordinal))
        {
            json = JsonSerializer.Serialize(new
            {
                authorization_endpoint = "https://login.microsoftonline.com/common/oauth2/v2.0/authorize",
                token_endpoint = "https://login.microsoftonline.com/common/oauth2/v2.0/token",
                issuer = "https://login.microsoftonline.com/" + Tenant + "/v2.0"
            });
        }
        else if (uri.AbsolutePath.EndsWith("/token", StringComparison.Ordinal))
        {
            Exchanges++;
            LastRequest = await request.Content!.ReadAsStringAsync(cancellationToken);
            if (BlockTokenRequests)
            {
                TokenRequestBlocked.TrySetResult();
                try
                {
                    await ReleaseTokenRequest.Task.WaitAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    TokenRequestCancelled.TrySetResult();
                    throw;
                }
            }

            status = Error is null ? HttpStatusCode.OK : HttpStatusCode.BadRequest;
            string idToken = Encode(new { alg = "none" }) + "." + Encode(new
            {
                aud = ApplicationId,
                iss = "https://login.microsoftonline.com/" + Tenant + "/v2.0",
                tid = ResourceTenant,
                oid = HomeUser,
                sub = HomeUser,
                preferred_username = "synthetic@example.test",
                exp = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()
            }) + ".synthetic";
            json = Error is not null ? JsonSerializer.Serialize(new { error = Error, error_description = "synthetic-private-error" }) :
                JsonSerializer.Serialize(new
                {
                    access_token = AccessSecret,
                    refresh_token = RefreshSecret,
                    token_type = "Bearer",
                    expires_in = ExpiresIn,
                    scope = GrantedScopes,
                    id_token = idToken,
                    client_info = Encode(new { uid = HomeUser, utid = Tenant })
                });
        }
        else
        {
            throw new InvalidOperationException("Unexpected synthetic MSAL endpoint.");
        }

        return new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private static string Encode<TValue>(TValue value)
    {
        return Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
