using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using MimeKit;
using MimeKit.IO;
using MimeKit.IO.Filters;
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
using Postfold.Protocols.Interfaces;
using Postfold.Protocols.Policies;
using Postfold.Protocols.Services;
using Postfold.Protocols.Stores;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Protocols.Adapters;

public sealed class ImapAdapter : IMailboxReader, IMessageContentReader, IMailboxMutator, ISentCopyStore,
    IRemoteDraftTransport, IIdleTransport, IMessageAttachmentReader
{
    private readonly MailEndpoint _endpoint;
    private readonly ICredentialStore _credentials;
    private readonly ImapClient _client = new ImapClient();
    private readonly SemaphoreSlim _session = new SemaphoreSlim(1, 1);
    private readonly long _maximumContentBytes;
    private ImmutableArray<MailboxSnapshot> _mailboxes = [];
    private FolderRoles? _roles;
    private readonly object _idleGate = new object();
    private readonly TimeProvider _time;
    private CancellationTokenSource? _idleDone;

    public ImapAdapter(AccountId account, MailEndpoint endpoint, ICredentialStore credentials, long maximumContentBytes)
        : this(account, endpoint, credentials, maximumContentBytes, TimeProvider.System)
    {
    }

    public ImapAdapter(
        AccountId account,
        MailEndpoint endpoint,
        ICredentialStore credentials,
        long maximumContentBytes,
        TimeProvider time
    )
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumContentBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(maximumContentBytes, int.MaxValue);
        Account = account;
        _endpoint = endpoint;
        _credentials = credentials;
        _maximumContentBytes = maximumContentBytes;
        _time = time;
        _client.Timeout = 30000;
    }

    public AccountId Account
    {
        get;
    }
    public Capabilities Capabilities { get; private set; } = new Capabilities(false, false, false, false, false, false);

    internal X509Certificate2? FixtureAuthority
    {
        set
        {
            if (value is not null)
            {
                _client.ServerCertificateValidationCallback = (_, certificate, _, errors) =>
                    FixtureCertificateTrust.Validate(value, certificate, errors);
            }
        }
    }

    public void RestoreMailboxes(ImmutableArray<MailboxSnapshot> mailboxes)
    {
        if (mailboxes.Any(item => item.Account != Account))
        {
            throw new ArgumentException("Mailbox catalog belongs to another account.", nameof(mailboxes));
        }

        _mailboxes = mailboxes;
    }

    public void RestoreCapabilities(Capabilities? capabilities)
    {
        if (capabilities is not null)
        {
            Capabilities = capabilities;
        }
    }

    public void SetRoles(FolderRoles? roles)
    {
        if (roles is not null && !roles.IsValid)
        {
            throw new ArgumentException("Invalid folder roles.", nameof(roles));
        }

        _roles = roles;
        if (roles is not null)
        {
            _mailboxes = _mailboxes.Select(roles.Apply).ToImmutableArray();
        }
    }

    public async Task<Outcome<ImmutableArray<MailboxSnapshot>>> DiscoverAsync(CancellationToken cancellationToken)
    {
        await AcquireSessionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Failure failure = await ConnectAsync(cancellationToken).ConfigureAwait(false);
            if (failure != Failure.None)
            {
                return Outcome<ImmutableArray<MailboxSnapshot>>.Fail(failure);
            }

            ImmutableArray<MailboxSnapshot>.Builder result = ImmutableArray.CreateBuilder<MailboxSnapshot>();
            foreach (FolderNamespace space in _client.PersonalNamespaces)
            {
                IList<IMailFolder> folders = await _client.GetFoldersAsync(space, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                foreach (IMailFolder folder in folders)
                {
                    result.Add(Snapshot(folder));
                }
            }

            _mailboxes = result.ToImmutable();
            return Outcome<ImmutableArray<MailboxSnapshot>>.Success(_mailboxes);
        }
        catch (Exception exception) when (Expected(exception))
        {
            return Outcome<ImmutableArray<MailboxSnapshot>>.Fail(Classify(exception));
        }
        finally
        {
            _session.Release();
        }
    }

    public async Task<Outcome<MetadataPage>> ReadAsync(
        string scope,
        Checkpoint? checkpoint,
        int limit,
        CancellationToken cancellationToken
    )
    {
        if (limit is < 1 or > 1000 || (checkpoint is not null && checkpoint.Account != Account))
        {
            return Outcome<MetadataPage>.Fail(Failure.Validation);
        }

        await AcquireSessionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Failure failure = await ConnectAsync(cancellationToken).ConfigureAwait(false);
            if (failure != Failure.None)
            {
                return Outcome<MetadataPage>.Fail(failure);
            }

            IMailFolder folder = await _client.GetFolderAsync(scope, cancellationToken).ConfigureAwait(false);
            if (Capabilities.QuickResync && checkpoint is { Complete: true, ModSequence: > 0 } &&
                uint.TryParse(checkpoint.Generation.Split(':')[0], out uint priorValidity))
            {
                await folder.OpenAsync(
                    FolderAccess.ReadOnly,
                    priorValidity,
                    checkpoint.ModSequence,
                    UniqueIdRange.All,
                    cancellationToken
                ).ConfigureAwait(false);
            }
            else
            {
                await folder.OpenAsync(FolderAccess.ReadOnly, cancellationToken).ConfigureAwait(false);
            }

            string validity = folder.UidValidity.ToString(CultureInfo.InvariantCulture);
            bool continuing = checkpoint is not null && !checkpoint.Complete &&
                checkpoint.Generation.StartsWith(validity + ":", StringComparison.Ordinal);
            string generation = continuing ? checkpoint!.Generation : validity + ":" + Guid.NewGuid().ToString("N");
            uint upper = continuing ? uint.Parse(checkpoint!.Cursor!, CultureInfo.InvariantCulture) :
                (folder.UidNext?.Id ?? 1) - 1;
            uint lower = upper > (uint)limit ? upper - (uint)limit + 1 : 1;
            ulong baseline = checkpoint is not null &&
                checkpoint.Generation.StartsWith(validity + ":", StringComparison.Ordinal) ? checkpoint.ModSequence : 0;
            ImmutableArray<MessageSnapshot>.Builder messages = ImmutableArray.CreateBuilder<MessageSnapshot>();
            ImmutableArray<ProviderIdentity>.Builder seen = ImmutableArray.CreateBuilder<ProviderIdentity>();
            if (upper > 0)
            {
                UniqueIdRange range = new UniqueIdRange(new UniqueId(lower), new UniqueId(upper));
                FetchRequest request = new FetchRequest(MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope |
                    MessageSummaryItems.Flags | MessageSummaryItems.Size | MessageSummaryItems.ModSeq);
                IList<UniqueId> present = await folder.SearchAsync(SearchQuery.Uids(range), cancellationToken)
                    .ConfigureAwait(false);
                foreach (UniqueId uid in present)
                {
                    if (uid.Id >= lower && uid.Id <= upper)
                    {
                        seen.Add(Identity(new ImapIdentity(Account, scope, folder.UidValidity, uid.Id)));
                    }
                }

                if (Capabilities.CondStore && baseline > 0)
                {
                    request.ChangedSince = baseline;
                }

                IList<IMessageSummary> summaries = await folder.FetchAsync(range, request, cancellationToken)
                    .ConfigureAwait(false);
                foreach (IMessageSummary summary in summaries.Where(item => item.UniqueId.Id >= lower &&
                    item.UniqueId.Id <= upper).OrderByDescending(item => item.UniqueId.Id))
                {
                    ImapIdentity imap = new ImapIdentity(Account, scope, folder.UidValidity, summary.UniqueId.Id);
                    messages.Add(new MessageSnapshot(
                        Account,
                        new MessageId(0),
                        Identity(imap),
                        summary.Envelope?.Subject ?? "",
                        summary.Envelope?.From.ToString() ?? "",
                        summary.Envelope?.MessageId,
                        summary.Envelope?.Date ?? DateTimeOffset.UnixEpoch,
                        summary.Size ?? 0,
                        (summary.Flags & MessageFlags.Seen) != 0,
                        (summary.Flags & MessageFlags.Flagged) != 0,
                        ContentAvailability.Missing,
                        PendingState.None
                    ));
                }
            }

            bool complete = upper == 0 || lower == 1;
            Checkpoint next = new Checkpoint(
                Account,
                scope,
                generation,
                complete ? null : (lower - 1).ToString(CultureInfo.InvariantCulture),
                complete ? folder.HighestModSeq : baseline,
                complete
            );
            return Outcome<MetadataPage>.Success(new MetadataPage(next, messages.ToImmutable(), [], true, seen.ToImmutable()));
        }
        catch (Exception exception) when (Expected(exception))
        {
            return Outcome<MetadataPage>.Fail(Classify(exception));
        }
        finally
        {
            _session.Release();
        }
    }

    public async Task<Outcome<Stream>> OpenAsync(ProviderIdentity identity, CancellationToken cancellationToken)
    {
        if (identity.Account != Account || identity.Imap is null || identity.Imap.Account != Account)
        {
            return Outcome<Stream>.Fail(Failure.Validation);
        }

        await AcquireSessionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Failure failure = await ConnectAsync(cancellationToken).ConfigureAwait(false);
            if (failure != Failure.None)
            {
                return Outcome<Stream>.Fail(failure);
            }

            IMailFolder folder = await _client.GetFolderAsync(identity.Imap.Mailbox, cancellationToken).ConfigureAwait(false);
            await folder.OpenAsync(FolderAccess.ReadOnly, cancellationToken).ConfigureAwait(false);
            if (folder.UidValidity != identity.Imap.UidValidity)
            {
                return Outcome<Stream>.Fail(Failure.NeedsReview);
            }

            // Partial retrieval bounds server-controlled literal allocation as well as downstream parsing.
            using Stream remote = await folder.GetStreamAsync(
                new UniqueId(identity.Imap.Uid),
                0,
                checked((int)_maximumContentBytes + 1),
                cancellationToken
            ).ConfigureAwait(false);
            using BoundedStream bounded = new BoundedStream(remote, _maximumContentBytes);
            MemoryStream owned = new MemoryStream();
            try
            {
                await bounded.CopyToAsync(owned, cancellationToken).ConfigureAwait(false);
                owned.Position = 0;
                return Outcome<Stream>.Success(owned);
            }
            catch
            {
                owned.Dispose();
                throw;
            }
        }
        catch (Exception exception) when (Expected(exception))
        {
            return Outcome<Stream>.Fail(Classify(exception));
        }
        finally
        {
            _session.Release();
        }
    }

    public async Task<Failure> ApplyAsync(MutationIntent intent, CancellationToken cancellationToken)
    {
        if (intent.Account != Account || intent.Provider.Account != Account || intent.Provider.Imap is null ||
            intent.Provider.Imap.Account != Account || intent.PayloadVersion != 1 || !Enum.IsDefined(intent.Kind))
        {
            return Failure.Validation;
        }

        await AcquireSessionAsync(cancellationToken).ConfigureAwait(false);
        bool effectMayHaveBegun = false;
        try
        {
            Failure failure = await ConnectAsync(cancellationToken).ConfigureAwait(false);
            if (failure != Failure.None)
            {
                return failure;
            }

            ImapIdentity identity = intent.Provider.Imap;
            IMailFolder folder = await _client.GetFolderAsync(identity.Mailbox, cancellationToken).ConfigureAwait(false);
            await folder.OpenAsync(FolderAccess.ReadWrite, cancellationToken).ConfigureAwait(false);
            if (folder.UidValidity != identity.UidValidity)
            {
                return Failure.NeedsReview;
            }

            UniqueId uid = new UniqueId(identity.Uid);
            IList<UniqueId> exists = await folder.SearchAsync(SearchQuery.Uids([uid]), cancellationToken)
                .ConfigureAwait(false);
            if (!exists.Contains(uid))
            {
                return Failure.NeedsReview;
            }

            effectMayHaveBegun = true;
            if (intent.Kind == MutationKind.Move)
            {
                if (intent.Destination is null)
                {
                    return Failure.Validation;
                }

                IMailFolder destination = await _client.GetFolderAsync(intent.Destination, cancellationToken)
                    .ConfigureAwait(false);
                if (Capabilities.NativeMove)
                {
                    await folder.MoveToAsync([uid], destination, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await folder.CopyToAsync([uid], destination, cancellationToken).ConfigureAwait(false);
                    if (!Capabilities.SelectiveExpunge)
                    {
                        return Failure.NeedsReview;
                    }

                    await folder.AddFlagsAsync([uid], MessageFlags.Deleted, true, cancellationToken).ConfigureAwait(false);
                    await folder.ExpungeAsync([uid], cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                MessageFlags flag = intent.Kind == MutationKind.SetRead ? MessageFlags.Seen : MessageFlags.Flagged;
                if (intent.Value)
                {
                    await folder.AddFlagsAsync([uid], flag, true, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await folder.RemoveFlagsAsync([uid], flag, true, cancellationToken).ConfigureAwait(false);
                }
            }

            return Failure.None;
        }
        catch (Exception exception) when (Expected(exception) || exception is OperationCanceledException)
        {
            if (effectMayHaveBegun)
            {
                return Failure.NeedsReview;
            }

            cancellationToken.ThrowIfCancellationRequested();
            return Classify(exception);
        }
        finally
        {
            _session.Release();
        }
    }

    public async Task<Outcome<Stream>> OpenBodyAsync(ProviderIdentity identity, CancellationToken cancellationToken)
    {
        if (identity.Account != Account || identity.Imap is null || identity.Imap.Account != Account)
        {
            return Outcome<Stream>.Fail(Failure.Validation);
        }

        await AcquireSessionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Failure failure = await ConnectAsync(cancellationToken).ConfigureAwait(false);
            if (failure != Failure.None)
            {
                return Outcome<Stream>.Fail(failure);
            }

            IMailFolder folder = await _client.GetFolderAsync(identity.Imap.Mailbox, cancellationToken).ConfigureAwait(false);
            await folder.OpenAsync(FolderAccess.ReadOnly, cancellationToken).ConfigureAwait(false);
            if (folder.UidValidity != identity.Imap.UidValidity)
            {
                return Outcome<Stream>.Fail(Failure.NeedsReview);
            }

            UniqueId uid = new UniqueId(identity.Imap.Uid);
            IList<IMessageSummary> metadata = await folder.FetchAsync(
                [uid],
                new FetchRequest(
    MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope | MessageSummaryItems.BodyStructure
),
                cancellationToken
            ).ConfigureAwait(false);
            IMessageSummary? summary = metadata.SingleOrDefault(item => item.UniqueId == uid);
            BodyPartText? body = summary?.TextBody ?? summary?.HtmlBody;
            if (body is null)
            {
                return Outcome<Stream>.Fail(Failure.NotFound);
            }

            if (body.Octets > _maximumContentBytes)
            {
                return Outcome<Stream>.Fail(Failure.Validation);
            }

            ContentEncoding encoding = body.ContentTransferEncoding?.ToLowerInvariant() switch
            {
                "7bit" => ContentEncoding.SevenBit,
                "8bit" => ContentEncoding.EightBit,
                "binary" => ContentEncoding.Binary,
                "base64" => ContentEncoding.Base64,
                "quoted-printable" => ContentEncoding.QuotedPrintable,
                _ => ContentEncoding.Default
            };
            if (encoding == ContentEncoding.Default)
            {
                return Outcome<Stream>.Fail(Failure.Validation);
            }

            string partSpecifier = body.PartSpecifier.Length == 0 ? "TEXT" : body.PartSpecifier;
            using Stream remote = await folder.GetStreamAsync(
                uid,
                partSpecifier,
                0,
                checked((int)_maximumContentBytes + 1),
                cancellationToken
            ).ConfigureAwait(false);
            using BoundedStream limited = new BoundedStream(remote, _maximumContentBytes);
            using MemoryStream encoded = new MemoryStream();
            await limited.CopyToAsync(encoded, cancellationToken).ConfigureAwait(false);
            encoded.Position = 0;
            using MimeMessage message = new MimeMessage();
            message.Subject = summary!.Envelope?.Subject ?? "";
            if (summary.Envelope?.MessageId is string headerId)
            {
                message.MessageId = headerId;
            }
            message.Date = summary.Envelope?.Date ?? DateTimeOffset.UnixEpoch;
            if (summary.Envelope is not null)
            {
                message.From.AddRange(summary.Envelope.From);
            }

            TextPart text = new TextPart(body.ContentType.MediaSubtype)
            {
                Content = new MimeContent(encoded, encoding),
                ContentTransferEncoding = encoding
            };
            text.ContentType.Charset = body.ContentType.Charset;
            message.Body = text;
            MemoryStream owned = new MemoryStream();
            try
            {
                using BoundedStream output = new BoundedStream(owned, _maximumContentBytes);
                await message.WriteToAsync(output, cancellationToken).ConfigureAwait(false);
                owned.Position = 0;
                return Outcome<Stream>.Success(owned);
            }
            catch
            {
                owned.Dispose();
                throw;
            }
        }
        catch (Exception exception) when (Expected(exception) || exception is FormatException)
        {
            return Outcome<Stream>.Fail(Classify(exception));
        }
        finally
        {
            _session.Release();
        }
    }

    public async Task<Outcome<ImmutableArray<AttachmentSnapshot>>> ReadAttachmentsAsync(ProviderIdentity identity, CancellationToken cancellationToken)
    {
        if (identity.Account != Account || identity.Imap is null || identity.Imap.Account != Account)
        {
            return Outcome<ImmutableArray<AttachmentSnapshot>>.Fail(Failure.Validation);
        }

        await AcquireSessionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Outcome<(IMailFolder Folder, IMessageSummary Summary)> source = await AttachmentSourceAsync(
                identity.Imap,
                cancellationToken
            ).ConfigureAwait(false);
            if (!source.IsSuccess)
            {
                return Outcome<ImmutableArray<AttachmentSnapshot>>.Fail(source.Failure);
            }

            ImmutableArray<AttachmentSnapshot>.Builder attachments = ImmutableArray.CreateBuilder<AttachmentSnapshot>();
            foreach (BodyPartBasic part in source.Value.Summary.Attachments)
            {
                if (attachments.Count == 200 || part.PartSpecifier.Length > 128 || part.FileName?.Length > 4096)
                {
                    return Outcome<ImmutableArray<AttachmentSnapshot>>.Fail(Failure.Validation);
                }

                attachments.Add(new AttachmentSnapshot(
                    "imap:" + part.PartSpecifier,
                    part.FileName ?? "attachment",
                    part.ContentType.MimeType,
                    part.Octets,
                    ContentAvailability.Missing
                ));
            }

            return Outcome<ImmutableArray<AttachmentSnapshot>>.Success(attachments.ToImmutable());
        }
        catch (Exception exception) when (Expected(exception))
        {
            return Outcome<ImmutableArray<AttachmentSnapshot>>.Fail(Classify(exception));
        }
        finally
        {
            _session.Release();
        }
    }

    public async Task<Failure> CopyAttachmentAsync(
        ProviderIdentity identity,
        string part,
        Stream destination,
        CancellationToken cancellationToken
    )
    {
        if (identity.Account != Account || identity.Imap is null || identity.Imap.Account != Account ||
            !part.StartsWith("imap:", StringComparison.Ordinal) || part.Length is < 6 or > 133)
        {
            return Failure.Validation;
        }

        string section = part[5..];
        if (!section.All(character => char.IsAsciiDigit(character) || character == '.'))
        {
            return Failure.Validation;
        }

        await AcquireSessionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Outcome<(IMailFolder Folder, IMessageSummary Summary)> source = await AttachmentSourceAsync(
                identity.Imap,
                cancellationToken
            ).ConfigureAwait(false);
            if (!source.IsSuccess)
            {
                return source.Failure;
            }

            BodyPartBasic? attachment = source.Value.Summary.Attachments.SingleOrDefault(item => item.PartSpecifier == section);
            if (attachment is null)
            {
                return Failure.NotFound;
            }

            if (attachment.Octets > _maximumContentBytes)
            {
                return Failure.Validation;
            }

            ContentEncoding encoding = attachment.ContentTransferEncoding?.ToLowerInvariant() switch
            {
                "7bit" => ContentEncoding.SevenBit,
                "8bit" => ContentEncoding.EightBit,
                "binary" => ContentEncoding.Binary,
                "base64" => ContentEncoding.Base64,
                "quoted-printable" => ContentEncoding.QuotedPrintable,
                _ => ContentEncoding.Default
            };
            if (encoding == ContentEncoding.Default)
            {
                return Failure.Validation;
            }

            using Stream remote = await source.Value.Folder.GetStreamAsync(
                new UniqueId(identity.Imap.Uid),
                section,
                0,
                checked((int)_maximumContentBytes + 1),
                cancellationToken
            ).ConfigureAwait(false);
            using BoundedStream encoded = new BoundedStream(remote, _maximumContentBytes);
            using FilteredStream decoded = new FilteredStream(encoded);
            if (encoding is ContentEncoding.Base64 or ContentEncoding.QuotedPrintable)
            {
                decoded.Add(DecoderFilter.Create(encoding));
            }

            using BoundedStream output = new BoundedStream(destination, _maximumContentBytes);
            await decoded.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            return encoded.Length == attachment.Octets ? Failure.None : Failure.Unavailable;
        }
        catch (Exception exception) when (Expected(exception))
        {
            return Classify(exception);
        }
        finally
        {
            _session.Release();
        }
    }

    public async Task<Failure> SaveAsync(FrozenSubmission submission, Stream content, CancellationToken cancellationToken)
    {
        if (submission.Account != Account)
        {
            return Failure.Validation;
        }

        await AcquireSessionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Failure failure = await ConnectAsync(cancellationToken).ConfigureAwait(false);
            if (failure != Failure.None)
            {
                return failure;
            }

            if (_mailboxes.Count(mailbox => mailbox.Role == MailboxRole.Sent && mailbox.Selectable) != 1)
            {
                return Failure.NeedsReview;
            }

            MailboxSnapshot destination = _mailboxes.Single(item => item.Role == MailboxRole.Sent && item.Selectable);
            IMailFolder sent = await _client.GetFolderAsync(destination.RemoteKey, cancellationToken).ConfigureAwait(false);

            using MimeMessage message = await MimeMessage.LoadAsync(content, cancellationToken).ConfigureAwait(false);
            await sent.AppendAsync(message, MessageFlags.Seen, cancellationToken).ConfigureAwait(false);
            return Failure.None;
        }
        catch (Exception exception) when (Expected(exception))
        {
            // An uncertain sent APPEND is reconciled separately; it can never return to SMTP.
            return Failure.NeedsReview;
        }
        finally
        {
            _session.Release();
        }
    }

    public Outcome<IRemoteDraftStore> SelectDraftMailbox(string mailbox, IBlobStore blobs)
    {
        MailboxSnapshot? selected = _mailboxes.SingleOrDefault(item => item.RemoteKey == mailbox);
        if (selected is null || !selected.Selectable || !selected.Writable)
        {
            return Outcome<IRemoteDraftStore>.Fail(Failure.Validation);
        }

        Capabilities = Capabilities with
        {
            RemoteDrafts = true
        };
        return Outcome<IRemoteDraftStore>.Success(new RemoteImapDraftStore(this, blobs, mailbox));
    }

    public IMailboxSignal? CreateIdleSignal()
    {
        return Capabilities.Idle ? new ImapIdleSignal(this) : null;
    }

    public async ValueTask DisposeAsync()
    {
        await AcquireSessionAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            _client.Dispose();
        }
        finally
        {
            _session.Release();
        }
    }

    private async Task<Outcome<(IMailFolder Folder, IMessageSummary Summary)>> AttachmentSourceAsync(
        ImapIdentity identity,
        CancellationToken cancellationToken
    )
    {
        Failure failure = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        if (failure != Failure.None)
        {
            return Outcome<(IMailFolder, IMessageSummary)>.Fail(failure);
        }

        IMailFolder folder = await _client.GetFolderAsync(identity.Mailbox, cancellationToken).ConfigureAwait(false);
        await folder.OpenAsync(FolderAccess.ReadOnly, cancellationToken).ConfigureAwait(false);
        if (folder.UidValidity != identity.UidValidity)
        {
            return Outcome<(IMailFolder, IMessageSummary)>.Fail(Failure.NeedsReview);
        }

        UniqueId uid = new UniqueId(identity.Uid);
        IList<IMessageSummary> metadata = await folder.FetchAsync([uid], new FetchRequest(
            MessageSummaryItems.UniqueId | MessageSummaryItems.BodyStructure
        ), cancellationToken).ConfigureAwait(false);
        IMessageSummary? summary = metadata.SingleOrDefault(item => item.UniqueId == uid);
        return summary is null ? Outcome<(IMailFolder, IMessageSummary)>.Fail(Failure.NotFound) :
            Outcome<(IMailFolder, IMessageSummary)>.Success((folder, summary));
    }

    async Task<Failure> IMailboxSignal.WaitAsync(
        string mailbox,
        TimeSpan maximumWait,
        CancellationToken cancellationToken
    )
    {
        if (!Capabilities.Idle || maximumWait <= TimeSpan.Zero || maximumWait > TimeSpan.FromMinutes(25))
        {
            return Failure.Validation;
        }

        await AcquireSessionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Failure failure = await ConnectAsync(cancellationToken).ConfigureAwait(false);
            if (failure != Failure.None)
            {
                return failure;
            }

            IMailFolder folder = await _client.GetFolderAsync(mailbox, cancellationToken).ConfigureAwait(false);
            await folder.OpenAsync(FolderAccess.ReadOnly, cancellationToken).ConfigureAwait(false);
            using CancellationTokenSource done = new CancellationTokenSource(maximumWait, _time);
            lock (_idleGate)
            {
                _idleDone = done;
            }

            EventHandler<EventArgs> changed = (_, _) => done.Cancel();
            folder.CountChanged += changed;
            folder.MessageFlagsChanged += FlagsChanged;
            folder.MessageExpunged += Expunged;
            try
            {
                await _client.IdleAsync(done.Token, cancellationToken).ConfigureAwait(false);
                return Failure.None;
            }
            finally
            {
                folder.CountChanged -= changed;
                folder.MessageFlagsChanged -= FlagsChanged;
                folder.MessageExpunged -= Expunged;
                lock (_idleGate)
                {
                    _idleDone = null;
                }
            }

            void FlagsChanged(object? sender, MessageFlagsChangedEventArgs args)
            {
                done.Cancel();
            }

            void Expunged(object? sender, MessageEventArgs args)
            {
                done.Cancel();
            }
        }
        catch (Exception exception) when (Expected(exception))
        {
            return Classify(exception);
        }
        finally
        {
            _session.Release();
        }
    }

    private Task AcquireSessionAsync(CancellationToken cancellationToken)
    {
        lock (_idleGate)
        {
            _idleDone?.Cancel();
        }

        return _session.WaitAsync(cancellationToken);
    }

    async Task<RemoteDraftResult> IRemoteDraftTransport.SaveDraftAsync(
        string mailbox,
        IBlobStore blobs,
        DraftSnapshot draft,
        Stream content,
        CancellationToken cancellationToken
    )
    {
        if (draft.Account != Account)
        {
            return new RemoteDraftResult(null, null, null, false, Failure.Validation);
        }

        await AcquireSessionAsync(cancellationToken).ConfigureAwait(false);
        bool appendStarted = false;
        try
        {
            Failure failure = await ConnectAsync(cancellationToken).ConfigureAwait(false);
            if (failure != Failure.None)
            {
                return new RemoteDraftResult(null, null, null, false, failure);
            }

            IMailFolder folder = await _client.GetFolderAsync(mailbox, cancellationToken).ConfigureAwait(false);
            await folder.OpenAsync(FolderAccess.ReadWrite, cancellationToken).ConfigureAwait(false);
            ImapIdentity? previous = draft.Remote?.Imap;
            if (previous is not null)
            {
                if (previous.Account != Account || previous.Mailbox != mailbox || previous.UidValidity != folder.UidValidity)
                {
                    return new RemoteDraftResult(null, null, null, false, Failure.NeedsReview);
                }

                using Stream existing = await folder.GetStreamAsync(
                    new UniqueId(previous.Uid),
                    0,
                    checked((int)_maximumContentBytes + 1),
                    cancellationToken
                ).ConfigureAwait(false);
                using BoundedStream bounded = new BoundedStream(existing, _maximumContentBytes);
                using MemoryStream remoteContent = new MemoryStream();
                await bounded.CopyToAsync(remoteContent, cancellationToken).ConfigureAwait(false);
                remoteContent.Position = 0;
                string version = Convert.ToHexString(SHA256.HashData(remoteContent));
                remoteContent.Position = 0;
                if (version != draft.RemoteVersion)
                {
                    BlobReference conflict = await blobs.WriteAsync(
                        Account,
                        remoteContent,
                        _maximumContentBytes,
                        protect: true,
                        cancellationToken
                    ).ConfigureAwait(false);
                    return new RemoteDraftResult(draft.Remote, version, conflict, false, Failure.NeedsReview);
                }
            }

            using MimeMessage message = await MimeMessage.LoadAsync(content, cancellationToken).ConfigureAwait(false);
            appendStarted = true;
            UniqueId? appended = await folder.AppendAsync(message, MessageFlags.Draft, cancellationToken).ConfigureAwait(false);
            if (appended is null)
            {
                return new RemoteDraftResult(null, null, null, true, Failure.NeedsReview);
            }

            ProviderIdentity identity = Identity(new ImapIdentity(Account, mailbox, folder.UidValidity, appended.Value.Id));
            using Stream written = await folder.GetStreamAsync(
                appended.Value,
                0,
                checked((int)_maximumContentBytes + 1),
                cancellationToken
            ).ConfigureAwait(false);
            using BoundedStream writtenBounded = new BoundedStream(written, _maximumContentBytes);
            byte[] hash = await SHA256.HashDataAsync(writtenBounded, cancellationToken).ConfigureAwait(false);
            if (previous is not null)
            {
                if (!Capabilities.SelectiveExpunge)
                {
                    return new RemoteDraftResult(identity, Convert.ToHexString(hash), null, true, Failure.NeedsReview);
                }

                await folder.AddFlagsAsync([new UniqueId(previous.Uid)], MessageFlags.Deleted, true, cancellationToken)
                    .ConfigureAwait(false);
                await folder.ExpungeAsync([new UniqueId(previous.Uid)], cancellationToken).ConfigureAwait(false);
            }

            return new RemoteDraftResult(identity, Convert.ToHexString(hash), null, false, Failure.None);
        }
        catch (Exception exception) when (Expected(exception) || exception is OperationCanceledException or FormatException)
        {
            if (!appendStarted)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            return new RemoteDraftResult(
                null,
                null,
                null,
                appendStarted,
                appendStarted ? Failure.NeedsReview : Classify(exception)
            );
        }
        finally
        {
            _session.Release();
        }
    }

    private async Task<Failure> ConnectAsync(CancellationToken cancellationToken)
    {
        if (!_client.IsConnected)
        {
            await _client.ConnectAsync(
                _endpoint.Host,
                _endpoint.Port,
                _endpoint.Tls == TlsMode.Implicit ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls,
                cancellationToken
            ).ConfigureAwait(false);
        }

        if (!_client.IsAuthenticated)
        {
            Outcome<Credential> credential = await _credentials.ReadAsync(_endpoint.CredentialKey, cancellationToken)
                .ConfigureAwait(false);
            if (!credential.IsSuccess)
            {
                return Failure.NeedsAuthentication;
            }

            if (credential.Value!.BearerToken)
            {
                await _client.AuthenticateAsync(
                    new SaslMechanismOAuth2(_endpoint.Username, credential.Value.Secret),
                    cancellationToken
                ).ConfigureAwait(false);
            }
            else
            {
                await _client.AuthenticateAsync(_endpoint.Username, credential.Value.Secret, cancellationToken)
                    .ConfigureAwait(false);
            }

            if ((_client.Capabilities & ImapCapabilities.QuickResync) != 0)
            {
                await _client.EnableQuickResyncAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        ImapCapabilities capabilities = _client.Capabilities;
        Capabilities = new Capabilities(
            (capabilities & ImapCapabilities.CondStore) != 0,
            (capabilities & ImapCapabilities.QuickResync) != 0,
            (capabilities & ImapCapabilities.Idle) != 0,
            (capabilities & ImapCapabilities.Move) != 0,
            (capabilities & ImapCapabilities.UidPlus) != 0,
            false
        );
        return Failure.None;
    }

    private MailboxSnapshot Snapshot(IMailFolder folder)
    {
        MailboxRole role = folder.FullName.Equals("INBOX", StringComparison.OrdinalIgnoreCase) ? MailboxRole.Inbox :
            (folder.Attributes & FolderAttributes.Sent) != 0 ? MailboxRole.Sent :
            (folder.Attributes & FolderAttributes.Drafts) != 0 ? MailboxRole.Drafts :
            (folder.Attributes & FolderAttributes.Trash) != 0 ? MailboxRole.Trash :
            (folder.Attributes & FolderAttributes.Archive) != 0 ? MailboxRole.Archive : MailboxRole.Unresolved;
        MailboxSnapshot snapshot = new MailboxSnapshot(
            Account,
            new FolderId(0),
            folder.FullName,
            folder.Name,
            role,
            (folder.Attributes & FolderAttributes.NoSelect) == 0,
            true,
            folder.UidValidity
        );
        return _roles is null ? snapshot : _roles.Apply(snapshot);
    }

    private static ProviderIdentity Identity(ImapIdentity identity)
    {
        return new ProviderIdentity(identity.Account, identity.Mailbox.Length.ToString(CultureInfo.InvariantCulture) +
            ":" + identity.Mailbox + ":" + identity.UidValidity.ToString(CultureInfo.InvariantCulture) + ":" +
            identity.Uid.ToString(CultureInfo.InvariantCulture), identity);
    }

    private static bool Expected(Exception exception)
    {
        return exception is IOException or System.Net.Sockets.SocketException or ImapCommandException or
            ImapProtocolException or SslHandshakeException or
            NotSupportedException or MessageNotFoundException or FolderNotFoundException or
            MailKit.Security.AuthenticationException or System.Security.Authentication.AuthenticationException;
    }

    private static Failure Classify(Exception exception)
    {
        return exception is MailKit.Security.AuthenticationException ? Failure.NeedsAuthentication :
            exception is SslHandshakeException or System.Security.Authentication.AuthenticationException or
                NotSupportedException ? Failure.TlsRejected :
            exception is InvalidDataException or ImapCommandException or FormatException ?
                Failure.Validation : exception is MessageNotFoundException or FolderNotFoundException ?
                Failure.NeedsReview : Failure.Transient;
    }
}
