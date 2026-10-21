using MimeKit;
using Postfold.Core.Enums;
using Postfold.Core.Interfaces;
using Postfold.Core.Models;
using Postfold.Core.Models.Accounts;
using Postfold.Core.Models.Drafts;
using Postfold.Core.Models.Mailboxes;
using Postfold.Core.Models.Messages;
using Postfold.Core.Models.Operations;
using Postfold.Core.Models.Outbox;
using Postfold.Core.Models.Synchronisation;
using Postfold.Protocols.Services;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Protocols.Adapters;

/// <summary>Account-wide Gmail labels/history mapped to the shared engine. All background authentication is silent.</summary>
public sealed class GmailAdapter : IMailboxReader, ICanonicalMailboxReader, IMessageContentReader,
    IMailboxMutator, IRemoteDraftStore, IMessageSubmitter, ISentCopyStore
{
    private const int MetadataBytes = 4194304;
    private readonly GoogleAccountBinding _binding;
    private readonly GmailApiClient _api;
    private readonly IBlobStore _blobs;
    private readonly long _maximumBytes;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
    private readonly CancellationTokenSource _shutdown = new CancellationTokenSource();

    public GmailAdapter(
        AccountId account,
        GoogleAccountBinding binding,
        IAccountAuthenticator? authentication,
        IBlobStore blobs,
        long maximumBytes,
        TimeProvider time
    ) : this(account, binding, authentication, blobs, maximumBytes, time, new HttpClient(new HttpClientHandler

    {
        AllowAutoRedirect = false
    }))
    {
    }

    internal GmailAdapter(
        AccountId account,
        GoogleAccountBinding binding,
        IAccountAuthenticator? authentication,
        IBlobStore blobs,
        long maximumBytes,
        TimeProvider time,
        HttpClient http,
        bool ownsHttp = true
    )
    {
        if (account.Value <= 0 || maximumBytes is <= 0 or > 33554432)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }
        Account = account;
        _binding = binding;
        _blobs = blobs;
        _maximumBytes = maximumBytes;
        _time = time;
        _api = new GmailApiClient(account, binding, authentication, http, time, ownsHttp);
    }

    public AccountId Account { get; }
    public Capabilities Capabilities { get; } = new Capabilities(false, false, false, true, false, true);

    public Task<Outcome<ImmutableArray<MailboxSnapshot>>> DiscoverAsync(
        CancellationToken cancellationToken
    )
    {
        return RunAsync(ReadLabelsAsync, cancellationToken);
    }

    public Task<Outcome<MetadataPage>> ReadAsync(
        string scope,
        Checkpoint? checkpoint,
        int limit,
        CancellationToken cancellationToken
    )
    {
        // This adapter is scheduled only through its canonical port; never manufacture mailbox copies.
        return Task.FromResult(Outcome<MetadataPage>.Fail(Failure.Unsupported));
    }

    public Task<Outcome<CanonicalPage>> ReadAsync(
        Checkpoint? checkpoint,
        int limit,
        CancellationToken cancellationToken
    )
    {
        return RunAsync(async token =>
        {
            if (limit is < 1 or > 1000 || checkpoint is not null &&
                (checkpoint.Account != Account || checkpoint.Scope != "$account"))
            {
                throw new GmailApiClient.RequestFailure(Failure.Validation, false);
            }
            Cursor cursor;
            string generation;
            if (checkpoint is null)
            {
                using JsonDocument profile = await GetAsync("profile", MetadataBytes, token).ConfigureAwait(false);
                if (!string.Equals(Text(profile.RootElement, "emailAddress"), _binding.Address, StringComparison.OrdinalIgnoreCase))
                {
                    throw new GmailApiClient.RequestFailure(Failure.NeedsReview, false);
                }
                cursor = new Cursor(1, Account.Value, _binding.Address, "list", HistoryId(profile.RootElement), null, []);
                generation = Guid.NewGuid().ToString("N");
            }
            else
            {
                cursor = ReadCursor(checkpoint.Cursor);
                generation = checkpoint.Generation;
            }

            ImmutableArray<MailboxSnapshot> labels = await ReadLabelsAsync(token).ConfigureAwait(false);
            string path = cursor.Phase == "list" ? "messages?includeSpamTrash=true" :
                "history?startHistoryId=" + Uri.EscapeDataString(cursor.Start);
            path += "&maxResults=" + Math.Min(limit, 500).ToString(CultureInfo.InvariantCulture);
            if (cursor.Page is not null)
            {
                path += "&pageToken=" + Uri.EscapeDataString(cursor.Page);
            }

            JsonDocument page;
            try
            {
                page = await GetAsync(path, MetadataBytes, token).ConfigureAwait(false);
            }
            catch (GmailApiClient.RequestFailure failure) when (failure.Failure == Failure.NotFound ||
                cursor.Page is not null && failure.Failure == Failure.Validation)
            {
                throw new GmailApiClient.RequestFailure(Failure.CursorExpired, false);
            }
            using (page)
            {
                JsonElement root = page.RootElement;
                HashSet<string> changed = new HashSet<string>(StringComparer.Ordinal);
                List<MembershipChange> removedLabels = [];
                if (cursor.Phase == "list")
                {
                    foreach (JsonElement message in Array(root, "messages", Math.Min(limit, 500)))
                    {
                        changed.Add(Text(message, "id"));
                    }
                }
                else
                {
                    foreach (JsonElement history in Array(root, "history", Math.Min(limit, 500)))
                    {
                        HistoryId(history, true);
                        foreach (JsonElement message in Array(history, "messages", 1000))
                        {
                            changed.Add(Text(message, "id"));
                        }
                        foreach (string kind in new[] { "messagesAdded", "messagesDeleted", "labelsAdded", "labelsRemoved" })
                        {
                            foreach (JsonElement change in Array(history, kind, 1000))
                            {
                                string key = Text(change.GetProperty("message"), "id");
                                changed.Add(key);
                                if (kind == "labelsRemoved")
                                {
                                    foreach (JsonElement label in Array(change, "labelIds", 1000))
                                    {
                                        removedLabels.Add(new MembershipChange(key, Identifier(label.GetString()), false));
                                    }
                                }
                            }
                        }
                    }
                }
                if (changed.Count > 1000)
                {
                    throw new InvalidDataException();
                }

                List<MessageSnapshot> messages = [];
                List<MembershipChange> memberships = [];
                List<string> deleted = [];
                foreach (string key in changed)
                {
                    try
                    {
                        using JsonDocument metadata = await GetAsync("messages/" + Uri.EscapeDataString(key) +
                            "?format=metadata&metadataHeaders=Subject&metadataHeaders=From&metadataHeaders=Message-ID", MetadataBytes, token).ConfigureAwait(false);
                        JsonElement message = metadata.RootElement;
                        if (Text(message, "id") != key)
                        {
                            throw new InvalidDataException();
                        }
                        HashSet<string> current = Array(message, "labelIds", 1000).Select(item => Identifier(item.GetString())).ToHashSet(StringComparer.Ordinal);
                        if (current.Any(id => !labels.Any(label => label.RemoteKey == id)))
                        {
                            // Labels changed during discovery: retry a whole page rather than commit a dangling membership.
                            throw new GmailApiClient.RequestFailure(Failure.Transient, false);
                        }
                        Dictionary<string, string> headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        foreach (JsonElement header in Array(message.GetProperty("payload"), "headers", 100))
                        {
                            string value = header.GetProperty("value").GetString() ?? "";
                            if (value.Length > 65536)
                            {
                                throw new InvalidDataException();
                            }
                            headers.TryAdd(Text(header, "name"), value);
                        }
                        long date = long.Parse(Text(message, "internalDate"), CultureInfo.InvariantCulture);
                        long size = message.GetProperty("sizeEstimate").GetInt64();
                        if (size < 0)
                        {
                            throw new InvalidDataException();
                        }
                        messages.Add(new MessageSnapshot(
                            Account,
                            default,
                            new ProviderIdentity(Account, key, null),
                            headers.GetValueOrDefault("Subject", ""),
                            headers.GetValueOrDefault("From", ""),
                            headers.GetValueOrDefault("Message-ID"),
                            DateTimeOffset.FromUnixTimeMilliseconds(date),
                            size,
                            !current.Contains("UNREAD"),
                            current.Contains("STARRED"),
                            ContentAvailability.Missing,
                            PendingState.None,
                            Text(message, "threadId")
                        ));
                        foreach (MailboxSnapshot label in labels)
                        {
                            memberships.Add(new MembershipChange(key, label.RemoteKey, current.Contains(label.RemoteKey)));
                        }
                    }
                    catch (GmailApiClient.RequestFailure failure) when (failure.Failure == Failure.NotFound)
                    {
                        deleted.Add(key);
                    }
                }
                // Current metadata wins over an older removal event (including remove/add in the same page).
                memberships.AddRange(removedLabels.Where(item => !memberships.Any(current =>
                    current.ProviderMessageKey == item.ProviderMessageKey && current.CollectionKey == item.CollectionKey)));

                string? next = root.TryGetProperty("nextPageToken", out JsonElement nextElement) ? Identifier(nextElement.GetString(), 2048) : null;
                bool complete = false;
                bool full = cursor.Phase != "history";
                if (next is not null)
                {
                    string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(next)));
                    if (cursor.Seen.Length >= 4096 || cursor.Seen.Contains(hash, StringComparer.Ordinal))
                    {
                        throw new InvalidDataException();
                    }
                    cursor = cursor with { Page = next, Seen = [.. cursor.Seen, hash] };
                }
                else if (cursor.Phase == "list")
                {
                    cursor = cursor with { Phase = "catchup", Page = null, Seen = [] };
                }
                else
                {
                    string latest = HistoryId(root);
                    if (ulong.Parse(latest, CultureInfo.InvariantCulture) < ulong.Parse(cursor.Start, CultureInfo.InvariantCulture))
                    {
                        throw new InvalidDataException();
                    }
                    cursor = cursor with { Phase = "history", Start = latest, Page = null, Seen = [] };
                    complete = true;
                }
                return new CanonicalPage(
                    new Checkpoint(Account, "$account", generation, JsonSerializer.Serialize(cursor), 0, complete),
                    labels,
                    messages.ToImmutableArray(),
                    memberships.ToImmutableArray(),
                    deleted.ToImmutableArray(),
                    full
                );
            }
        }, cancellationToken);
    }

    public Task<Outcome<Stream>> OpenBodyAsync(
        ProviderIdentity message,
        CancellationToken cancellationToken
    )
    {
        return OpenAsync(message, cancellationToken);
    }

    public Task<Outcome<Stream>> OpenAsync(
        ProviderIdentity message,
        CancellationToken cancellationToken
    )
    {
        return RunAsync<Stream>(async token =>
        {
            Validate(message);
            using JsonDocument raw = await GetAsync("messages/" + Uri.EscapeDataString(message.Key) + "?format=raw", RawJsonLimit(), token).ConfigureAwait(false);
            if (Text(raw.RootElement, "id") != message.Key)
            {
                throw new InvalidDataException();
            }
            return new MemoryStream(Decode(raw.RootElement), false);
        }, cancellationToken);
    }

    public async Task<Failure> ApplyAsync(
        MutationIntent intent,
        CancellationToken cancellationToken
    )
    {
        Outcome<bool> result = await RunAsync(async token =>
        {
            Validate(intent.Provider);
            if (intent.Account != Account || intent.PayloadVersion != 1)
            {
                throw new InvalidDataException();
            }
            List<string> add = [];
            List<string> remove = [];
            if (intent.Kind is MutationKind.SetRead or MutationKind.SetFlagged)
            {
                string label = intent.Kind == MutationKind.SetRead ? "UNREAD" : "STARRED";
                bool present = intent.Kind == MutationKind.SetRead ? !intent.Value : intent.Value;
                (present ? add : remove).Add(label);
            }
            else if (intent.Kind == MutationKind.Move)
            {
                // A null target is explicit archive: remove INBOX, retain all other memberships.
                if (intent.Destination is not null)
                {
                    ImmutableArray<MailboxSnapshot> labels = await ReadLabelsAsync(token).ConfigureAwait(false);
                    if (!labels.Any(item => item.RemoteKey == intent.Destination && item.Writable))
                    {
                        throw new GmailApiClient.RequestFailure(Failure.Unsupported, false);
                    }
                    add.Add(intent.Destination);
                }
                if (intent.Destination != "INBOX")
                {
                    remove.Add("INBOX");
                }
            }
            else
            {
                throw new GmailApiClient.RequestFailure(Failure.Unsupported, false);
            }
            using JsonDocument reply = await _api.RequestAsync(
                HttpMethod.Post,
                "messages/" + Uri.EscapeDataString(intent.Provider.Key) + "/modify",
                new { addLabelIds = add, removeLabelIds = remove },
                MetadataBytes,
                token
            ).ConfigureAwait(false);
            if (Text(reply.RootElement, "id") != intent.Provider.Key)
            {
                throw new GmailApiClient.RequestFailure(Failure.NeedsReview, true);
            }
            return true;
        }, cancellationToken).ConfigureAwait(false);
        return result.Failure;
    }

    public async Task<RemoteDraftResult> SaveAsync(
        DraftSnapshot draft,
        Stream content,
        CancellationToken cancellationToken
    )
    {
        Outcome<RemoteDraftResult> result = await RunAsync(async token =>
        {
            if (draft.Account != Account || draft.Content.Account != Account)
            {
                throw new GmailApiClient.RequestFailure(Failure.Validation, false);
            }
            if (draft.Remote is not null)
            {
                Validate(draft.Remote);
                using JsonDocument remote = await GetAsync("drafts/" + Uri.EscapeDataString(draft.Remote.Key) + "?format=raw", RawJsonLimit(), token).ConfigureAwait(false);
                if (Text(remote.RootElement, "id") != draft.Remote.Key)
                {
                    throw new InvalidDataException();
                }
                JsonElement message = remote.RootElement.GetProperty("message");
                byte[] bytes = Decode(message);
                string version = DraftVersion(message, bytes);
                if (draft.RemoteVersion == version)
                {
                    return new RemoteDraftResult(draft.Remote, version, null, true, Failure.NeedsReview);
                }
                using MemoryStream retained = new MemoryStream(bytes, false);
                BlobReference conflict = await _blobs.WriteAsync(Account, retained, _maximumBytes, true, token).ConfigureAwait(false);
                // Gmail has no conditional draft replacement. Even a matching preflight version cannot
                // prevent a concurrent edit being overwritten. Preserve both versions and require review.
                return new RemoteDraftResult(draft.Remote, version, conflict, false, Failure.NeedsReview);
            }
            byte[] frozen = await ReadBytesAsync(content, token).ConfigureAwait(false);
            try
            {
                using JsonDocument reply = await _api.RequestAsync(
                    HttpMethod.Post,
                    "drafts",
                    new { message = new { raw = Encode(frozen) } },
                    MetadataBytes,
                    token
                ).ConfigureAwait(false);
                string id = Text(reply.RootElement, "id");
                JsonElement message = reply.RootElement.GetProperty("message");
                return new RemoteDraftResult(new ProviderIdentity(Account, id, null), DraftVersion(message, frozen), null, false, Failure.None);
            }
            catch (GmailApiClient.RequestFailure failure)
            {
                return new RemoteDraftResult(null, null, null, failure.Uncertain, failure.Uncertain ? Failure.NeedsReview : failure.Failure);
            }
            catch (Exception exception) when (exception is InvalidDataException or KeyNotFoundException or InvalidOperationException)
            {
                return new RemoteDraftResult(null, null, null, true, Failure.NeedsReview);
            }
        }, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? result.Value! : new RemoteDraftResult(null, null, null, false, result.Failure);
    }

    public async Task<SubmissionOutcome> SubmitAsync(
        FrozenSubmission submission,
        Stream mime,
        CancellationToken cancellationToken
    )
    {
        try
        {
            return await SubmitCoreAsync(submission, mime, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // RequestAsync converts cancellation after a possibly started POST to uncertain evidence.
            // Cancellation reaching here happened before that side-effect boundary.
            return Submission(submission, false, false, Failure.Transient);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _api.Dispose();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<SubmissionOutcome> SubmitCoreAsync(
        FrozenSubmission submission,
        Stream mime,
        CancellationToken cancellationToken
    )
    {
        Outcome<SubmissionOutcome> result = await RunAsync(async token =>
        {
            if (submission.Account != Account || submission.Mime.Account != Account || submission.Recipients.IsDefaultOrEmpty)
            {
                return Submission(submission, false, false, Failure.Validation);
            }
            byte[] frozen = await ReadBytesAsync(mime, token).ConfigureAwait(false);
            if (frozen.LongLength != submission.Mime.Length || Convert.ToHexString(SHA256.HashData(frozen)) != submission.Mime.Sha256)
            {
                return Submission(submission, false, false, Failure.Validation);
            }
            using MemoryStream input = new MemoryStream(frozen, false);
            ParserOptions options = ParserOptions.Default.Clone();
            options.MaxMimeDepth = 32;
            using MimeMessage parsed = await MimeMessage.LoadAsync(options, input, token).ConfigureAwait(false);
            string[] recipients = parsed.To.Mailboxes.Concat(parsed.Cc.Mailboxes).Concat(parsed.Bcc.Mailboxes)
                .Select(item => item.Address).ToArray();
            // messages.send derives its envelope from headers. Never silently change frozen bytes or recipients.
            if (!recipients.Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(submission.Recipients.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase) ||
                parsed.From.Mailboxes.Count() != 1 || !string.Equals(parsed.From.Mailboxes.Single().Address, submission.Sender, StringComparison.OrdinalIgnoreCase))
            {
                return Submission(submission, false, false, Failure.Validation);
            }
            try
            {
                using JsonDocument reply = await _api.RequestAsync(HttpMethod.Post, "messages/send", new { raw = Encode(frozen) }, MetadataBytes, token).ConfigureAwait(false);
                string id = Text(reply.RootElement, "id");
                Text(reply.RootElement, "threadId");
                return Submission(submission, true, false, Failure.None) with { Receipt = id };
            }
            catch (GmailApiClient.RequestFailure failure)
            {
                return Submission(submission, false, failure.Uncertain, failure.Uncertain ? Failure.NeedsReview : failure.Failure) with
                {
                    Phase = failure.Uncertain ? SubmissionPhase.AwaitingAcknowledgement :
                        failure.Attempted ? SubmissionPhase.Acknowledged : SubmissionPhase.BeforeConnect
                };
            }
            catch (Exception exception) when (exception is InvalidDataException or KeyNotFoundException or InvalidOperationException)
            {
                return Submission(submission, false, true, Failure.NeedsReview);
            }
        }, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? result.Value! : Submission(submission, false, false, result.Failure);
    }

    Task<Failure> ISentCopyStore.SaveAsync(
        FrozenSubmission submission,
        Stream content,
        CancellationToken cancellationToken
    )
    {
        // Gmail stores SENT on acceptance. This port is invoked only after the shared outbox's accepted claim.
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(submission.Account == Account ? Failure.None : Failure.Validation);
    }

    private async Task<Outcome<TValue>> RunAsync<TValue>(
        Func<CancellationToken, Task<TValue>> action,
        CancellationToken cancellationToken
    )
    {
        using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2), _time);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token, timeout.Token);
        await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            return Outcome<TValue>.Success(await action(linked.Token).ConfigureAwait(false));
        }
        catch (GmailApiClient.RequestFailure failure)
        {
            return Outcome<TValue>.Fail(failure.Uncertain ? Failure.NeedsReview : failure.Failure);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or KeyNotFoundException or
            InvalidOperationException or FormatException or ArgumentOutOfRangeException or OverflowException)
        {
            return Outcome<TValue>.Fail(Failure.Corrupt);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ImmutableArray<MailboxSnapshot>> ReadLabelsAsync(
        CancellationToken cancellationToken
    )
    {
        using JsonDocument reply = await GetAsync("labels", MetadataBytes, cancellationToken).ConfigureAwait(false);
        List<MailboxSnapshot> labels = [];
        foreach (JsonElement label in Array(reply.RootElement, "labels", 1000))
        {
            string id = Text(label, "id");
            string type = Text(label, "type");
            if (type is not ("system" or "user") || labels.Any(item => item.RemoteKey == id))
            {
                throw new InvalidDataException();
            }
            MailboxRole role = id switch
            {
                "INBOX" => MailboxRole.Inbox,
                "SENT" => MailboxRole.Sent,
                "DRAFT" => MailboxRole.Drafts,
                "TRASH" => MailboxRole.Trash,
                _ => MailboxRole.Unresolved
            };
            bool writable = type == "user" || id is "INBOX" or "TRASH" or "SPAM";
            labels.Add(new MailboxSnapshot(Account, default, id, Text(label, "name"), role, true, writable, 0));
        }
        if (labels.Count == 0)
        {
            throw new InvalidDataException();
        }
        return labels.ToImmutableArray();
    }

    private Task<JsonDocument> GetAsync(
        string path,
        long maximumBytes,
        CancellationToken cancellationToken
    )
    {
        return _api.RequestAsync(HttpMethod.Get, path, null, maximumBytes, cancellationToken);
    }

    private Cursor ReadCursor(
        string? value
    )
    {
        if (value is null || value.Length > 300000)
        {
            throw new InvalidDataException();
        }
        Cursor? cursor = JsonSerializer.Deserialize<Cursor>(value);
        if (cursor is null || cursor.Version != 1 || cursor.Account != Account.Value || cursor.Subject != _binding.Address ||
            cursor.Phase is not ("list" or "catchup" or "history") || cursor.Seen is null || cursor.Seen.Length > 4096 ||
            !ulong.TryParse(cursor.Start, NumberStyles.None, CultureInfo.InvariantCulture, out ulong history) || history == 0 ||
            cursor.Page is not null && (cursor.Page.Length is < 1 or > 2048))
        {
            throw new InvalidDataException();
        }
        return cursor;
    }

    private void Validate(
        ProviderIdentity identity
    )
    {
        if (identity.Account != Account || identity.Imap is not null)
        {
            throw new GmailApiClient.RequestFailure(Failure.Validation, false);
        }
        Identifier(identity.Key);
    }

    private byte[] Decode(
        JsonElement message
    )
    {
        string raw = Text(message, "raw", checked((int)((_maximumBytes + 2) / 3 * 4)));
        string unpadded = raw.TrimEnd('=');
        if (raw.Length - unpadded.Length > 2 || raw.Length != unpadded.Length && raw.Length % 4 != 0 ||
            unpadded.Length % 4 == 1 || (long)unpadded.Length * 6 / 8 > _maximumBytes ||
            unpadded.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')))
        {
            throw new InvalidDataException();
        }
        byte[] bytes = Convert.FromBase64String(unpadded.Replace('-', '+').Replace('_', '/').PadRight((unpadded.Length + 3) / 4 * 4, '='));
        if (bytes.Length == 0 || bytes.LongLength > _maximumBytes || Encode(bytes) != unpadded)
        {
            throw new InvalidDataException();
        }
        return bytes;
    }

    private async Task<byte[]> ReadBytesAsync(
        Stream source,
        CancellationToken cancellationToken
    )
    {
        using MemoryStream content = new MemoryStream();
        using BoundedStream bounded = new BoundedStream(source, _maximumBytes);
        await bounded.CopyToAsync(content, cancellationToken).ConfigureAwait(false);
        if (content.Length == 0)
        {
            throw new InvalidDataException();
        }
        return content.ToArray();
    }

    private long RawJsonLimit()
    {
        return (_maximumBytes + 2) / 3 * 4 + 65536;
    }

    private static string Encode(
        byte[] bytes
    )
    {
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static string DraftVersion(
        JsonElement message,
        byte[] bytes
    )
    {
        return "v1:" + Text(message, "id") + ":" + Convert.ToHexString(SHA256.HashData(bytes));
    }

    private static string Text(
        JsonElement element,
        string name,
        int maximum = 2048
    )
    {
        return Identifier(element.GetProperty(name).GetString(), maximum);
    }

    private static string Identifier(
        string? value,
        int maximum = 2048
    )
    {
        return !string.IsNullOrEmpty(value) && value.Length <= maximum ? value : throw new InvalidDataException();
    }

    private static string HistoryId(
        JsonElement element,
        bool record = false
    )
    {
        string value = Text(element, record ? "id" : "historyId");
        if (!ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out ulong parsed) || parsed == 0)
        {
            throw new InvalidDataException();
        }
        return value;
    }

    private static IEnumerable<JsonElement> Array(
        JsonElement element,
        string property,
        int maximum
    )
    {
        if (!element.TryGetProperty(property, out JsonElement array))
        {
            return [];
        }
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > maximum)
        {
            throw new InvalidDataException();
        }
        return array.EnumerateArray();
    }

    private static SubmissionOutcome Submission(
        FrozenSubmission submission,
        bool accepted,
        bool uncertain,
        Failure failure
    )
    {
        return new SubmissionOutcome(
            accepted ? SubmissionState.Accepted : uncertain ? SubmissionState.Uncertain : SubmissionState.Rejected,
            accepted ? SubmissionPhase.Acknowledged : uncertain ? SubmissionPhase.AwaitingAcknowledgement : SubmissionPhase.BeforeConnect,
            submission.Recipients.Select(address => new RecipientEvidence(
                address,
                accepted ? RecipientState.Accepted : uncertain ? RecipientState.Uncertain : RecipientState.NotAccepted,
                null
            )).ToImmutableArray(),
            failure
        );
    }

    private sealed record Cursor(int Version, long Account, string Subject, string Phase, string Start, string? Page, string[] Seen);
}
