using NUnit.Framework;
using Postfold.Core.Models.Accounts;
using Postfold.Desktop.Services.Authentication;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Foundation.Tests.Fixtures.Authentication;

/// <summary>Stateful synthetic Gmail effects. Real Google authenticator; no provider network access.</summary>
internal sealed class GmailFixture : IAsyncDisposable
{
    internal const string Mime = "From: sender@synthetic.invalid\r\nTo: first@synthetic.invalid\r\nSubject: frozen synthetic\r\nMessage-ID: <same@synthetic.invalid>\r\n\r\nfrozen synthetic body\r\n";
    internal static readonly GoogleAccountBinding Binding = new GoogleAccountBinding("synthetic-gmail.apps.googleusercontent.com", "synthetic@example.test");
    private readonly Dictionary<string, (string Id, string Raw)> _drafts = [];

    internal GmailFixture()
    {
        Authentication.ProviderRequest = RespondAsync;
    }

    internal AuthenticationFixture Authentication { get; } = new AuthenticationFixture();
    internal Dictionary<string, string[]> Messages { get; } = new Dictionary<string, string[]>

    {
        ["one"] = ["INBOX", "Label_1", "UNREAD"],
        ["two"] = ["INBOX"]
    };
    internal string[][] Pages { get; set; } = [["one"], ["two"]];
    internal string History { get; set; } = "{\"historyId\":\"120\"}";
    internal List<(string Method, string Path, string? Body)> Requests { get; } = [];
    internal Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage?>>? Intercept { get; set; }
    internal Action<int>? AfterList { get; set; }
    internal int Sends { get; private set; }
    internal int DraftWrites { get; private set; }
    internal int DraftUpdates { get; private set; }
    internal int DraftDeletes { get; private set; }
    internal string? SentRaw { get; private set; }
    internal bool LoseSendAcknowledgement { get; set; }
    internal bool LoseDraftAcknowledgement { get; set; }
    internal bool MalformedSendAcknowledgement { get; set; }
    internal TaskCompletionSource? SendEntered { get; set; }
    internal TaskCompletionSource? DraftEntered { get; set; }
    internal string Raw { get; set; } = Encode(Mime);

    public ValueTask DisposeAsync()
    {
        return Authentication.DisposeAsync();
    }

    internal GoogleAccountAuthenticator Authenticator()
    {
        return new GoogleAccountAuthenticator(
            new AccountId(1),
            Binding.ClientId,
            Authentication,
            Authentication,
            Authentication.Http,
            TimeProvider.System,
            TimeSpan.FromSeconds(20),
            Binding.Address
        );
    }

    internal HttpClient Http()
    {
        return new HttpClient(Authentication, false);
    }

    internal async Task SeedAsync()
    {
        await using GoogleAccountAuthenticator auth = Authenticator();
        Assert.That((await auth.AcquireAsync(true, CancellationToken.None)).IsSuccess, Is.True);
    }

    internal void ReplaceDraft(string id, string mime)
    {
        _drafts[id] = ("replaced-message", Encode(mime));
    }

    internal void ChangeDraftVersion(
        string id
    )
    {
        (string message, string raw) = _drafts[id];
        _drafts[id] = (message + "-edited", raw);
    }

    internal void NormalizeDraft(
        string id
    )
    {
        (string message, string raw) = _drafts[id];
        string padded = raw.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight((padded.Length + 3) / 4 * 4, '=');
        string mime = Encoding.UTF8.GetString(Convert.FromBase64String(padded));
        _drafts[id] = (message, Encode("X-Synthetic-Normalization: true\r\n" + mime));
    }

    internal static HttpResponseMessage Reply(
        string json,
        HttpStatusCode status = HttpStatusCode.OK
    )
    {
        return new HttpResponseMessage(status)

        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    internal static string Encode(
        string mime
    )
    {
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(mime)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private async Task<HttpResponseMessage> RespondAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        Assert.That(request.RequestUri!.Scheme, Is.EqualTo("https"));
        Assert.That(request.RequestUri.Host, Is.EqualTo("gmail.googleapis.com"));
        Assert.That(request.Headers.Authorization!.Scheme, Is.EqualTo("Bearer"));
        Assert.That(request.Headers.Authorization.Parameter, Does.StartWith("synthetic-"));
        string path = request.RequestUri.PathAndQuery.Replace("/gmail/v1/users/me/", "", StringComparison.Ordinal);
        string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add((request.Method.Method, path, body));
        if (Intercept is not null && await Intercept(request, cancellationToken) is HttpResponseMessage intercepted)
        {
            return intercepted;
        }
        if (path == "labels")
        {
            return Reply(JsonSerializer.Serialize(new
            {
                labels = new[]
            {
                new { id = "INBOX", name = "Inbox", type = "system" },
                new { id = "Label_1", name = "Synthetic label", type = "user" },
                new { id = "UNREAD", name = "Unread", type = "system" },
                new { id = "STARRED", name = "Starred", type = "system" },
                new { id = "SENT", name = "Sent", type = "system" },
                new { id = "DRAFT", name = "Drafts", type = "system" },
                new { id = "TRASH", name = "Trash", type = "system" }
            }
            }));
        }
        if (path.StartsWith("messages?", StringComparison.Ordinal))
        {
            Dictionary<string, string> query = AuthenticationFixture.Query(request.RequestUri);
            int page = query.TryGetValue("pageToken", out string? index) ? int.Parse(index) : 0;
            string[] ids = Pages[page];
            AfterList?.Invoke(page);
            return Reply(JsonSerializer.Serialize(page + 1 < Pages.Length ?
                new { messages = ids.Select(id => new { id }), nextPageToken = (page + 1).ToString() } :
                (object)new { messages = ids.Select(id => new { id }) }));
        }
        if (path.StartsWith("history?", StringComparison.Ordinal))
        {
            return Reply(History);
        }
        if (path == "messages/send")
        {
            Sends++;
            using JsonDocument json = JsonDocument.Parse(body!);
            SentRaw = json.RootElement.GetProperty("raw").GetString();
            if (SendEntered is not null)
            {
                SendEntered.TrySetResult();
                await new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task.WaitAsync(cancellationToken);
            }
            if (LoseSendAcknowledgement)
            {
                throw new HttpRequestException("Synthetic lost acknowledgement.");
            }
            return Reply(MalformedSendAcknowledgement ? "{}" : "{\"id\":\"sent-1\",\"threadId\":\"thread-1\"}");
        }
        if (path.StartsWith("messages/", StringComparison.Ordinal))
        {
            string id = path.Split('/', '?')[1];
            if (!Messages.TryGetValue(id, out string[]? labels))
            {
                return Reply("{}", HttpStatusCode.NotFound);
            }
            if (path.EndsWith("/modify", StringComparison.Ordinal))
            {
                using JsonDocument json = JsonDocument.Parse(body!);
                Messages[id] = labels.Except(json.RootElement.GetProperty("removeLabelIds").EnumerateArray().Select(item => item.GetString()!))
                    .Union(json.RootElement.GetProperty("addLabelIds").EnumerateArray().Select(item => item.GetString()!)).ToArray();
            }
            return Reply(JsonSerializer.Serialize(new
            {
                id,
                threadId = "thread-" + id,
                labelIds = Messages[id],
                historyId = "120",
                internalDate = "1700000000000",
                sizeEstimate = 200,
                raw = Raw,
                payload = new
                {
                    headers = new[]
                {
                    new { name = "Subject", value = "Synthetic " + id },
                    new { name = "From", value = "sender@synthetic.invalid" },
                    new { name = "Message-ID", value = "<same@synthetic.invalid>" }
                }
                }
            }));
        }
        if (path.StartsWith("drafts?", StringComparison.Ordinal))
        {
            Dictionary<string, string> query = AuthenticationFixture.Query(request.RequestUri);
            int offset = query.TryGetValue("pageToken", out string? pageToken) ? int.Parse(pageToken) : 0;
            int limit = int.Parse(query["maxResults"]);
            object[] drafts = _drafts.Skip(offset).Take(limit).Select(item => (object)new { id = item.Key, message = new { id = item.Value.Id } }).ToArray();
            return Reply(JsonSerializer.Serialize(offset + drafts.Length < _drafts.Count ?
                new { drafts, nextPageToken = (offset + drafts.Length).ToString() } : (object)new { drafts }));
        }
        if (path == "drafts")
        {
            DraftWrites++;
            using JsonDocument json = JsonDocument.Parse(body!);
            string id = "draft-" + DraftWrites;
            string message = "draft-message-" + DraftWrites;
            _drafts[id] = (message, json.RootElement.GetProperty("message").GetProperty("raw").GetString()!);
            if (DraftEntered is not null)
            {
                DraftEntered.TrySetResult();
                await new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task.WaitAsync(cancellationToken);
            }
            if (LoseDraftAcknowledgement)
            {
                throw new HttpRequestException("Synthetic lost draft acknowledgement.");
            }
            return Reply(JsonSerializer.Serialize(new { id, message = new { id = message } }));
        }
        if (path.StartsWith("drafts/", StringComparison.Ordinal))
        {
            string id = path.Split('/', '?')[1];
            if (request.Method == HttpMethod.Delete)
            {
                DraftDeletes++;
                bool existed = _drafts.Remove(id);
                if (LoseDraftAcknowledgement && existed)
                {
                    throw new HttpRequestException("Synthetic lost deletion acknowledgement.");
                }
                return Reply("{}", existed ? HttpStatusCode.OK : HttpStatusCode.NotFound);
            }
            if (request.Method == HttpMethod.Put)
            {
                DraftUpdates++;
                using JsonDocument json = JsonDocument.Parse(body!);
                _drafts[id] = ("updated-message-" + DraftUpdates, json.RootElement.GetProperty("message").GetProperty("raw").GetString()!);
                if (DraftEntered is not null)
                {
                    DraftEntered.TrySetResult();
                    await new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task.WaitAsync(cancellationToken);
                }
                if (LoseDraftAcknowledgement)
                {
                    throw new HttpRequestException("Synthetic lost update acknowledgement.");
                }
            }
            (string message, string raw) = _drafts[id];
            return Reply(JsonSerializer.Serialize(new { id, message = new { id = message, raw } }));
        }
        throw new InvalidOperationException("Unexpected synthetic Gmail request.");
    }
}
