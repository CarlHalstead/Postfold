using Postfold.Core.Enums;
using Postfold.Core.Interfaces;
using Postfold.Core.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Desktop.Services.Authentication;

/// <summary>One-use IPv4 loopback receiver. The caller supplies the bounded attempt/shutdown token.</summary>
public sealed class GoogleAuthorizationAttempt : IDisposable
{
    private readonly TcpListener _listener;
    private readonly string _state = RandomValue();
    private int _used;

    public GoogleAuthorizationAttempt()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Server.ExclusiveAddressUse = true;
        _listener.Start(1);
        Redirect = new Uri("http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port.ToString(CultureInfo.InvariantCulture) + "/");
    }

    public Uri Redirect { get; }
    public string Verifier { get; } = RandomValue();

    public Uri AuthorizationUri(string clientId)
    {
        string challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(Verifier)));
        return new Uri("https://accounts.google.com/o/oauth2/v2/auth?response_type=code&client_id=" +
            Uri.EscapeDataString(clientId) + "&redirect_uri=" + Uri.EscapeDataString(Redirect.AbsoluteUri) +
            "&scope=" + Uri.EscapeDataString(GoogleAccountAuthenticator.Scope) +
            "&state=" + _state + "&code_challenge=" + challenge +
            "&code_challenge_method=S256&access_type=offline&prompt=consent");
    }

    public async Task<Outcome<string>> ReceiveAsync(ISystemBrowser browser, string clientId, CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _used, 1) != 0)
        {
            return Outcome<string>.Fail(Failure.Validation);
        }

        try
        {
            Failure opened = await browser.OpenAsync(AuthorizationUri(clientId), cancellationToken).ConfigureAwait(false);
            if (opened != Failure.None)
            {
                return Outcome<string>.Fail(opened);
            }

            using TcpClient client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            using NetworkStream stream = client.GetStream();
            byte[] request = new byte[8192];
            int count = 0;
            while (count < request.Length)
            {
                int read = await stream.ReadAsync(request.AsMemory(count, 1), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                count += read;
                if (count >= 4 && request[count - 4] == 13 && request[count - 3] == 10 &&
                    request[count - 2] == 13 && request[count - 1] == 10)
                {
                    break;
                }
            }

            Outcome<string> result = Validate(Encoding.ASCII.GetString(request, 0, count));
            const string Body = "Return to Postfold. Close this tab.";
            byte[] response = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\n" +
                "Cache-Control: no-store\r\nContent-Security-Policy: default-src 'none'\r\n" +
                "X-Content-Type-Options: nosniff\r\nConnection: close\r\nContent-Length: " +
                Encoding.ASCII.GetByteCount(Body).ToString(CultureInfo.InvariantCulture) + "\r\n\r\n" + Body);
            await stream.WriteAsync(response, cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally
        {
            _listener.Stop();
        }
    }

    public void Dispose()
    {
        _listener.Stop();
    }

    private Outcome<string> Validate(string request)
    {
        string[] lines = request.Split("\r\n", StringSplitOptions.None);
        string[] first = lines[0].Split(' ');
        if (first.Length != 3 || first[0] != "GET" || first[2] != "HTTP/1.1" ||
            !first[1].StartsWith("/?", StringComparison.Ordinal) || first[1].Contains('#') ||
            !request.EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            return Outcome<string>.Fail(Failure.Validation);
        }

        int hosts = 0;
        foreach (string line in lines)
        {
            if (line.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))
            {
                hosts++;
                if (line[5..].Trim() != Redirect.Authority)
                {
                    return Outcome<string>.Fail(Failure.Validation);
                }
            }
        }

        Dictionary<string, string> query = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string pair in first[1][2..].Split('&'))
        {
            string[] parts = pair.Split('=', 2);
            if (parts.Length != 2 || !query.TryAdd(Uri.UnescapeDataString(parts[0]), Uri.UnescapeDataString(parts[1].Replace('+', ' '))))
            {
                return Outcome<string>.Fail(Failure.Validation);
            }
        }

        if (hosts != 1 || !query.TryGetValue("state", out string? state) || state != _state ||
            query.TryGetValue("iss", out string? issuer) && issuer != "https://accounts.google.com" ||
            query.ContainsKey("error") == query.ContainsKey("code"))
        {
            return Outcome<string>.Fail(Failure.Validation);
        }

        if (query.TryGetValue("error", out string? error))
        {
            return Outcome<string>.Fail(error is "admin_policy_enforced" or "org_internal" ?
                Failure.PolicyBlocked : Failure.NeedsAuthentication);
        }

        return string.IsNullOrWhiteSpace(query["code"]) ? Outcome<string>.Fail(Failure.Validation) :
            Outcome<string>.Success(query["code"]);
    }

    private static string RandomValue()
    {
        return Base64Url(RandomNumberGenerator.GetBytes(32));
    }

    private static string Base64Url(byte[] bytes)
    {
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
