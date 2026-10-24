using Postfold.Core.Enums;
using Postfold.Core.Interfaces;
using Postfold.Core.Models;
using Postfold.Core.Models.Accounts;
using Postfold.Core.Models.Drafts;
using Postfold.Core.Models.Mailboxes;
using Postfold.Core.Models.Messages;
using Postfold.Core.Models.Mime;
using Postfold.Core.Models.Operations;
using Postfold.Core.Models.Outbox;
using Postfold.Core.Models.Search;
using System;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Sync.Services;

public sealed class MailApplication : IMailApplication
{
    private readonly ILocalMailQuery _query;
    private readonly IBlobStore _blobs;
    private readonly IMimePipeline _mime;
    private readonly IMimeDraftReader _draftReader;
    private readonly IOutboxStore _outbox;
    private readonly IOperationStore _operations;
    private readonly IAccountCapabilityStore _capabilities;
    private readonly ISynchronisationStore _attachmentStore;
    private readonly IAccountEngines _engines;
    private readonly long _maximumBytes;
    private readonly ConcurrentDictionary<AccountId, SemaphoreSlim> _synchronisation = new ConcurrentDictionary<AccountId, SemaphoreSlim>();

    public MailApplication(
        IClientCatalog catalog,
        ILocalMailQuery query,
        IBlobStore blobs,
        IMimePipeline mime,
        IAccountEngines engines,
        long maximumBytes,
        IMimeDraftReader draftReader,
        IOutboxStore outbox,
        IOperationStore operations,
        IAccountCapabilityStore capabilities,
        ISynchronisationStore attachmentStore
    )
    {
        Catalog = catalog;
        _query = query;
        _blobs = blobs;
        _mime = mime;
        _draftReader = draftReader;
        _outbox = outbox;
        _operations = operations;
        _capabilities = capabilities;
        _attachmentStore = attachmentStore;
        _engines = engines;
        _maximumBytes = maximumBytes;
    }

    public IClientCatalog Catalog { get; }

    public Task<Outcome<SearchPage>> SearchAsync(SearchRequest request, CancellationToken cancellationToken)
    {
        return _query.SearchAsync(request, cancellationToken);
    }

    public async Task<Outcome<MimeSnapshot>> ReadAsync(
        MessageSnapshot message,
        bool retrieve,
        CancellationToken cancellationToken
    )
    {
        Outcome<BlobReference> body = await _query.ReadBodyAsync(
            message.Account,
            message.Id,
            cancellationToken
        ).ConfigureAwait(false);
        if (!body.IsSuccess && retrieve && _engines.Find(message.Account) is IMailAccountEngine engine)
        {
            Failure fetched = await engine.FetchBodyAsync(message, cancellationToken).ConfigureAwait(false);
            if (fetched != Failure.None)
            {
                return Outcome<MimeSnapshot>.Fail(fetched);
            }

            body = await _query.ReadBodyAsync(message.Account, message.Id, cancellationToken).ConfigureAwait(false);
        }

        if (!body.IsSuccess)
        {
            return Outcome<MimeSnapshot>.Fail(body.Failure);
        }

        await using Stream source = await _blobs.OpenAsync(body.Value!, cancellationToken).ConfigureAwait(false);
        Outcome<MimeSnapshot> parsed = await _mime.ParseAsync(source, cancellationToken).ConfigureAwait(false);
        if (parsed.IsSuccess && parsed.Value!.Attachments.IsEmpty)
        {
            return Outcome<MimeSnapshot>.Success(parsed.Value with
            {
                Attachments = await _query.ReadAttachmentsAsync(
                    message.Account,
                    message.Id,
                    cancellationToken
                ).ConfigureAwait(false)
            });
        }

        return parsed;
    }

    public async Task<Failure> SaveAttachmentAsync(
        MessageSnapshot message,
        string part,
        Stream destination,
        CancellationToken cancellationToken
    )
    {
        Outcome<BlobReference> body = await _query.ReadBodyAsync(
            message.Account,
            message.Id,
            cancellationToken
        ).ConfigureAwait(false);
        if (body.IsSuccess)
        {
            await using Stream source = await _blobs.OpenAsync(body.Value!, cancellationToken).ConfigureAwait(false);
            Outcome<bool> copied = await _mime.CopyAttachmentAsync(
                source,
                part,
                destination,
                cancellationToken
            ).ConfigureAwait(false);
            if (copied.IsSuccess || copied.Failure != Failure.NotFound)
            {
                return copied.Failure;
            }
        }

        IMailAccountEngine? engine = _engines.Find(message.Account);
        return engine is null ? Failure.Unavailable : await engine.CopyAttachmentAsync(
            message,
            part,
            destination,
            cancellationToken
        ).ConfigureAwait(false);
    }

    public async Task<Outcome<MimeHtmlDocument>> ReadHtmlAsync(
        MessageSnapshot message,
        bool retrieve,
        CancellationToken cancellationToken
    )
    {
        Outcome<BlobReference> cached = await _query.ReadBodyAsync(
            message.Account,
            message.Id,
            cancellationToken
        ).ConfigureAwait(false);
        if (cached.IsSuccess)
        {
            await using Stream source = await _blobs.OpenAsync(cached.Value!, cancellationToken).ConfigureAwait(false);
            Outcome<MimeHtmlDocument> document = await _mime.ReadHtmlAsync(source, cancellationToken).ConfigureAwait(false);
            if (document.IsSuccess || !retrieve || document.Failure != Failure.NotFound)
            {
                return document;
            }
        }

        if (!retrieve || _engines.Find(message.Account) is not IMailAccountEngine engine)
        {
            return Outcome<MimeHtmlDocument>.Fail(cached.IsSuccess ? Failure.NotFound : cached.Failure);
        }

        Failure fetched = await engine.FetchFullMessageAsync(message, cancellationToken).ConfigureAwait(false);
        if (fetched != Failure.None)
        {
            return Outcome<MimeHtmlDocument>.Fail(fetched);
        }

        return await ReadHtmlAsync(message, false, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Outcome<ImmutableArray<AttachmentSnapshot>>> ReadAttachmentsAsync(
        MessageSnapshot message,
        bool retrieve,
        CancellationToken cancellationToken
    )
    {
        if (!retrieve)
        {
            return Outcome<ImmutableArray<AttachmentSnapshot>>.Success(await _query.ReadAttachmentsAsync(
                message.Account,
                message.Id,
                cancellationToken
            ).ConfigureAwait(false));
        }

        IMailAccountEngine? engine = _engines.Find(message.Account);
        if (engine is null)
        {
            return Outcome<ImmutableArray<AttachmentSnapshot>>.Fail(Failure.Unavailable);
        }

        Outcome<ImmutableArray<AttachmentSnapshot>> attachments = await engine.ReadAttachmentsAsync(
            message,
            cancellationToken
        ).ConfigureAwait(false);
        if (attachments.IsSuccess)
        {
            Failure stored = await _attachmentStore.StoreAttachmentsAsync(
                message,
                attachments.Value,
                cancellationToken
            ).ConfigureAwait(false);
            if (stored != Failure.None)
            {
                return Outcome<ImmutableArray<AttachmentSnapshot>>.Fail(stored);
            }
        }

        return attachments;
    }

    public Task<Failure> MutateAsync(
        MessageSnapshot message,
        MutationKind kind,
        bool value,
        string? destination,
        CancellationToken cancellationToken
    )
    {
        IMailAccountEngine? engine = _engines.Find(message.Account);
        if (kind == MutationKind.Move && engine is not null &&
            !engine.Capabilities.NativeMove && !engine.Capabilities.SelectiveExpunge)
        {
            return Task.FromResult(Failure.Unsupported);
        }
        return engine is null ? Task.FromResult(Failure.Unavailable) : engine.QueueMutationAsync(
            new MutationIntent(
    new OperationId(Guid.NewGuid()),
    message.Account,
    message.Id,
    message.Provider,
    kind,
    value,
    destination
),
            cancellationToken
        );
    }

    public async Task<Failure> SynchroniseAsync(AccountId account, CancellationToken cancellationToken)
    {
        SemaphoreSlim gate = _synchronisation.GetOrAdd(account, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await SynchroniseAccountAsync(account, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<Outcome<DraftSnapshot>> SaveDraftAsync(
        AccountId account,
        Guid id,
        ComposeContent content,
        CancellationToken cancellationToken
    )
    {
        IMailAccountEngine? engine = _engines.Find(account);
        if (engine is null)
        {
            return Outcome<DraftSnapshot>.Fail(Failure.Unavailable);
        }

        ComposeContent serializable = content with
        {
            Recipients = content.Recipients.IsDefault ? [] : content.Recipients,
            Attachments = content.Attachments.IsDefault ? [] : content.Attachments
        };
        using MemoryStream source = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(serializable));
        return await engine.SaveDraftAsync(id, source, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Outcome<DraftSnapshot>> SaveDraftRevisionAsync(
        DraftSnapshot expected,
        ComposeContent content,
        CancellationToken cancellationToken
    )
    {
        IMailAccountEngine? engine = _engines.Find(expected.Account);
        if (engine is null)
        {
            return Outcome<DraftSnapshot>.Fail(Failure.Unavailable);
        }
        ComposeContent serializable = content with
        {
            Recipients = content.Recipients.IsDefault ? [] : content.Recipients,
            Attachments = content.Attachments.IsDefault ? [] : content.Attachments
        };
        using MemoryStream source = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(serializable));
        return await engine.SaveDraftRevisionAsync(expected, source, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Outcome<ComposeContent>> ReadDraftAsync(DraftSnapshot draft, CancellationToken cancellationToken)
    {
        await using Stream source = await _blobs.OpenAsync(draft.Content, cancellationToken).ConfigureAwait(false);
        try
        {
            ComposeContent? content = await JsonSerializer.DeserializeAsync<ComposeContent>(
                source,
                cancellationToken: cancellationToken
            ).ConfigureAwait(false);
            return content is null ? Outcome<ComposeContent>.Fail(Failure.Corrupt) : Outcome<ComposeContent>.Success(content);
        }
        catch (JsonException)
        {
            await using Stream mime = await _blobs.OpenAsync(draft.Content, cancellationToken).ConfigureAwait(false);
            return await _draftReader.ReadComposeAsync(draft.Account, mime, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task<Failure> DeleteDraftAsync(
        DraftSnapshot draft,
        CancellationToken cancellationToken
    )
    {
        IMailAccountEngine? engine = _engines.Find(draft.Account);
        return engine is null ? Task.FromResult(Failure.Unavailable) : engine.DeleteDraftAsync(draft, cancellationToken);
    }

    public Task<Failure> PushDraftAsync(AccountId account, Guid draft, CancellationToken cancellationToken)
    {
        IMailAccountEngine? engine = _engines.Find(account);
        return engine is null ? Task.FromResult(Failure.Unavailable) : engine.PushDraftAsync(draft, cancellationToken);
    }

    public Task<Outcome<DraftSnapshot>> CloseDraftAsync(
        AccountId account,
        Guid draft,
        bool synchronise,
        CancellationToken cancellationToken
    )
    {
        IMailAccountEngine? engine = _engines.Find(account);
        return engine is null ? Task.FromResult(Outcome<DraftSnapshot>.Fail(Failure.Unavailable)) :
            engine.CloseDraftAsync(draft, synchronise, cancellationToken);
    }

    public Task WaitForDraftSyncAsync(
        AccountId account,
        Guid draft,
        CancellationToken cancellationToken
    )
    {
        return _engines.Find(account)?.WaitForDraftSyncAsync(draft, cancellationToken) ?? Task.CompletedTask;
    }

    public Task<Outcome<DraftReviewSnapshot>> ReviewDraftAsync(
        DraftSnapshot draft,
        CancellationToken cancellationToken
    )
    {
        IMailAccountEngine? engine = _engines.Find(draft.Account);
        return engine is null ? Task.FromResult(Outcome<DraftReviewSnapshot>.Fail(Failure.Unavailable)) :
            engine.ReviewDraftAsync(draft, cancellationToken);
    }

    public Task<Failure> CopyReviewedDraftAsync(
        DraftReviewSnapshot review,
        CancellationToken cancellationToken
    )
    {
        IMailAccountEngine? engine = _engines.Find(review.Draft.Account);
        return engine is null ? Task.FromResult(Failure.Unavailable) : engine.CopyReviewedDraftAsync(review, cancellationToken);
    }

    public async Task<Outcome<ComposeAttachment>> ImportAttachmentAsync(
        AccountId account,
        string name,
        Stream source,
        CancellationToken cancellationToken
    )
    {
        try
        {
            BlobReference blob = await _blobs.WriteAsync(
                account,
                source,
                _maximumBytes,
                true,
                cancellationToken
            ).ConfigureAwait(false);
            return Outcome<ComposeAttachment>.Success(new ComposeAttachment(
                Path.GetFileName(name),
                "application/octet-stream",
                blob
            ));
        }
        catch (InvalidDataException)
        {
            return Outcome<ComposeAttachment>.Fail(Failure.Validation);
        }
    }

    public Task<Outcome<FrozenSubmission>> SendAsync(
        AccountId account,
        ComposeContent content,
        Guid draft,
        Guid? predecessor,
        CancellationToken cancellationToken
    )
    {
        IMailAccountEngine? engine = _engines.Find(account);
        return engine is null ? Task.FromResult(Outcome<FrozenSubmission>.Fail(Failure.Unavailable)) :
            engine.QueueSubmissionAsync(content, draft, predecessor ?? content.Predecessor, cancellationToken);
    }

    public Task<Failure> ProcessOutboxAsync(AccountId account, CancellationToken cancellationToken)
    {
        IMailAccountEngine? engine = _engines.Find(account);
        return engine is null ? Task.FromResult(Failure.Unavailable) : engine.ProcessOutboxAsync(cancellationToken);
    }

    public Task<Failure> SubmitAsync(AccountId account, Guid submission, CancellationToken cancellationToken)
    {
        IMailAccountEngine? engine = _engines.Find(account);
        return engine is null ? Task.FromResult(Failure.Unavailable) : engine.SubmitAsync(submission, cancellationToken);
    }

    public async Task<Outcome<ComposeContent>> PrepareResendAsync(OutboxEntry entry, CancellationToken cancellationToken)
    {
        ImmutableArray<OutboxEntry> entries = await Catalog.OutboxAsync(
            entry.Submission.Account,
            cancellationToken
        ).ConfigureAwait(false);
        OutboxEntry? current = entries.SingleOrDefault(item => item.Submission.Id == entry.Submission.Id);
        if (current is null || current.Revision != entry.Revision || current.State is not
            (SubmissionState.Uncertain or SubmissionState.PartiallyAccepted or SubmissionState.Rejected or
            SubmissionState.Cancelled))
        {
            return Outcome<ComposeContent>.Fail(Failure.NeedsReview);
        }

        await using Stream source = await _blobs.OpenAsync(
            current.Submission.Mime,
            cancellationToken
        ).ConfigureAwait(false);
        Outcome<MimeSnapshot> parsed = await _mime.ParseAsync(source, cancellationToken).ConfigureAwait(false);
        if (!parsed.IsSuccess)
        {
            return Outcome<ComposeContent>.Fail(parsed.Failure);
        }

        ImmutableArray<ComposeAttachment>.Builder attachments = ImmutableArray.CreateBuilder<ComposeAttachment>();
        foreach (AttachmentSnapshot attachment in parsed.Value!.Attachments)
        {
            await using Stream mime = await _blobs.OpenAsync(
                current.Submission.Mime,
                cancellationToken
            ).ConfigureAwait(false);
            using MemoryStream decoded = new MemoryStream();
            Outcome<bool> copied = await _mime.CopyAttachmentAsync(
                mime,
                attachment.Part,
                decoded,
                cancellationToken
            ).ConfigureAwait(false);
            if (!copied.IsSuccess)
            {
                return Outcome<ComposeContent>.Fail(copied.Failure);
            }

            decoded.Position = 0;
            BlobReference blob = await _blobs.WriteAsync(
                current.Submission.Account,
                decoded,
                _maximumBytes,
                true,
                cancellationToken
            ).ConfigureAwait(false);
            attachments.Add(new ComposeAttachment(attachment.Name, attachment.MediaType, blob));
        }

        return Outcome<ComposeContent>.Success(new ComposeContent(
            current.Submission.Sender,
            current.Submission.Recipients,
            parsed.Value.Subject,
            parsed.Value.PlainText ?? "",
            DateTimeOffset.UtcNow,
            Guid.NewGuid().ToString("N") + "@postfold.invalid",
            attachments.ToImmutable(),
            Predecessor: current.Submission.Id
        ));
    }

    public Task<Failure> StoreSentCopyAsync(OutboxEntry entry, CancellationToken cancellationToken)
    {
        IMailAccountEngine? engine = _engines.Find(entry.Submission.Account);
        return engine is null ? Task.FromResult(Failure.Unavailable) :
            engine.StoreSentCopyAsync(entry.Submission.Id, cancellationToken);
    }

    public async Task<Failure> CancelQueuedAsync(OutboxEntry entry, CancellationToken cancellationToken)
    {
        return await _outbox.CancelQueuedAsync(
            entry.Submission.Id,
            entry.Submission.Account,
            entry.Revision,
            cancellationToken
        ).ConfigureAwait(false) ? Failure.None : Failure.NeedsReview;
    }

    public async Task<Failure> RetrySentCopyAsync(OutboxEntry entry, CancellationToken cancellationToken)
    {
        if (entry.State != SubmissionState.SentCopyPending ||
            !await _outbox.PrepareSentCopyRetryAsync(
                entry.Submission.Id,
                entry.Submission.Account,
                entry.Revision,
                cancellationToken
            ).ConfigureAwait(false))
        {
            return Failure.NeedsReview;
        }

        return await StoreSentCopyAsync(
            entry with
            {
                State = SubmissionState.Accepted,
                Revision = entry.Revision + 1
            },
            cancellationToken
        ).ConfigureAwait(false);
    }

    public async Task<Failure> ResolveCommandAsync(OperationEntry entry, bool retry, CancellationToken cancellationToken)
    {
        return await _operations.ResolveAsync(
            entry.Intent.Id,
            entry.Intent.Account,
            entry.Revision,
            retry,
            cancellationToken
        ).ConfigureAwait(false) ? Failure.None : Failure.NeedsReview;
    }

    private async Task<Failure> SynchroniseAccountAsync(
        AccountId account,
        CancellationToken cancellationToken
    )
    {
        IMailAccountEngine? engine = _engines.Find(account);
        if (engine is null)
        {
            return Failure.Unavailable;
        }

        if (engine.Mailboxes.IsDefaultOrEmpty)
        {
            return Failure.NeedsReview;
        }

        foreach (MailboxSnapshot mailbox in engine.Mailboxes)
        {
            if (!mailbox.Selectable)
            {
                continue;
            }

            Failure result = await engine.ReconcileAsync(mailbox.RemoteKey, cancellationToken).ConfigureAwait(false);
            if (result != Failure.None)
            {
                return result;
            }
        }

        await _capabilities.SaveCapabilitiesAsync(account, engine.Capabilities, cancellationToken).ConfigureAwait(false);
        Failure mutations = await engine.ProcessMutationsAsync(cancellationToken).ConfigureAwait(false);
        return mutations == Failure.None ? await engine.SynchroniseDraftsAsync(cancellationToken).ConfigureAwait(false) : mutations;
    }
}
