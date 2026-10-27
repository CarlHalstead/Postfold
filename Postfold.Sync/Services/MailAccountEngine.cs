using Postfold.Core.Enums;
using Postfold.Core.Interfaces;
using Postfold.Core.Models;
using Postfold.Core.Models.Accounts;
using Postfold.Core.Models.Drafts;
using Postfold.Core.Models.Mailboxes;
using Postfold.Core.Models.Messages;
using Postfold.Core.Models.Operations;
using Postfold.Core.Models.Outbox;
using Postfold.Core.Models.Search;
using Postfold.Core.Models.Synchronisation;
using Postfold.Sync.Services.Work;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Sync.Services;

public sealed class MailAccountEngine : IMailAccountEngine
{
    private readonly IMailboxReader _reader;
    private readonly IMessageContentReader _content;
    private readonly IMailboxMutator? _mutator;
    private readonly IRemoteDraftStore? _drafts;
    private readonly ISentCopyStore? _sent;
    private readonly IMessageSubmitter _submitter;
    private readonly ISynchronisationStore _syncStore;
    private readonly IOperationStore _operations;
    private readonly IDraftStore _draftStore;
    private readonly IOutboxStore _outbox;
    private readonly ILocalMailQuery _query;
    private readonly IBlobStore _blobs;
    private readonly IMimePipeline _mime;
    private readonly IAccountScheduler _scheduler;
    private readonly IWorkPolicy _policy;
    private readonly IRetryDelayPolicy _retry;
    private readonly TimeProvider _time;
    private readonly EngineLimits _limits;
    private readonly ICacheStore _cache;
    private readonly SemaphoreSlim _localSaves = new SemaphoreSlim(1, 1);
    private readonly SemaphoreSlim _signalCalls = new SemaphoreSlim(1, 1);
    private readonly CancellationTokenSource _shutdown = new CancellationTokenSource();
    private readonly List<(Guid Draft, Task Work)> _draftUpdates = [];
    private readonly IMailboxSignal? _signal;
    private bool _stopping;

    public MailAccountEngine(
        IMailboxReader reader,
        IMessageContentReader content,
        IMailboxMutator? mutator,
        IRemoteDraftStore? drafts,
        ISentCopyStore? sent,
        IMessageSubmitter submitter,
        ISynchronisationStore syncStore,
        IOperationStore operations,
        IDraftStore draftStore,
        IOutboxStore outbox,
        ILocalMailQuery query,
        IBlobStore blobs,
        IMimePipeline mime,
        IAccountScheduler scheduler,
        IWorkPolicy policy,
        IRetryDelayPolicy retry,
        TimeProvider time,
        EngineLimits limits,
        ICacheStore cache,
        ImmutableArray<MailboxSnapshot> mailboxes,
        IMailboxSignal? signal = null
    )
    {
        _reader = reader;
        _content = content;
        _mutator = mutator;
        _drafts = drafts;
        _sent = sent;
        _submitter = submitter;
        _syncStore = syncStore;
        _operations = operations;
        _draftStore = draftStore;
        _outbox = outbox;
        _query = query;
        _blobs = blobs;
        _mime = mime;
        _scheduler = scheduler;
        _policy = policy;
        _retry = retry;
        _time = time;
        _limits = limits;
        _cache = cache;
        Account = reader.Account;
        Mailboxes = mailboxes;
        _signal = signal;
    }

    public AccountId Account { get; }
    public ImmutableArray<MailboxSnapshot> Mailboxes { get; private set; }
    public Capabilities Capabilities => _reader.Capabilities;
    public bool AccountWideReconciliation => _reader is ICanonicalMailboxReader;

    public event EventHandler? MetadataCommitted;

    public async Task<Failure> ReconcileAsync(string mailbox, CancellationToken cancellationToken)
    {
        if (_stopping || !_policy.AllowNetwork)
        {
            return Failure.Unavailable;
        }
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        cancellationToken = linked.Token;

        MailboxSnapshot? selected = Mailboxes.SingleOrDefault(item => item.RemoteKey == mailbox && item.Selectable);
        if (selected is null)
        {
            return Failure.Validation;
        }

        ICanonicalMailboxReader? canonical = _reader as ICanonicalMailboxReader;
        IAccountWork work = canonical is null ?
            new ReconciliationWork(_reader, _syncStore, selected, _limits.MetadataBatch) :
            new CanonicalReconciliationWork(canonical, _syncStore, _limits.MetadataBatch);
        Checkpoint? checkpoint;
        do
        {
            Failure result = await _scheduler.EnqueueAsync(
                Account,
                work,
                WorkPriority.Reconciliation,
                cancellationToken
            ).ConfigureAwait(false);
            if (result != Failure.None)
            {
                return result;
            }

            MetadataCommitted?.Invoke(this, EventArgs.Empty);
            checkpoint = await _syncStore.ReadCheckpointAsync(Account, canonical is null ? mailbox : "$account", cancellationToken).ConfigureAwait(false);
        }
        while (checkpoint is { Complete: false });

        if (canonical is not null && _syncStore is IClientCatalog catalog)
        {
            Mailboxes = (await catalog.MailboxesAsync(cancellationToken).ConfigureAwait(false))
                .Where(item => item.Account == Account).ToImmutableArray();
        }

        if (_mutator is not null)
        {
            Failure mutations = await ProcessMutationsAsync(cancellationToken).ConfigureAwait(false);
            if (mutations != Failure.None)
            {
                return mutations;
            }
        }

        if (_policy.AllowOptionalWork && _limits.PrefetchBatch > 0)
        {
            Outcome<SearchPage> recent = await _query.SearchAsync(new SearchRequest(
                Account,
                false,
                "",
                null,
                false,
                Math.Min(200, _limits.PrefetchBatch)
            ), cancellationToken).ConfigureAwait(false);
            foreach (MessageSnapshot message in recent.Value!.Messages)
            {
                if (message.Availability != ContentAvailability.Available &&
                    message.Date >= _time.GetUtcNow().AddDays(-_limits.PrefetchDays) &&
                    (canonical is not null || message.Provider.Imap?.Mailbox == mailbox))
                {
                    await _scheduler.EnqueueAsync(
                        Account,
                        new BodyFetchWork(message, _content, _blobs, _mime, _syncStore, _limits.MaximumMimeBytes),
                        WorkPriority.Prefetch,
                        cancellationToken
                    ).ConfigureAwait(false);
                }
            }
        }

        await _cache.EvictAsync(Account, _limits.CacheBudgetBytes, cancellationToken).ConfigureAwait(false);
        return Failure.None;
    }

    public async Task<Failure> WaitForChangesAsync(
        string mailbox,
        TimeSpan maximumWait,
        CancellationToken cancellationToken
    )
    {
        if (_signal is null || !_policy.AllowNetwork)
        {
            return Failure.Unavailable;
        }

        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _shutdown.Token
        );
        await _signalCalls.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            Failure result = await _signal.WaitAsync(mailbox, maximumWait, linked.Token).ConfigureAwait(false);
            return result == Failure.None ? await ReconcileAsync(mailbox, linked.Token).ConfigureAwait(false) : result;
        }
        finally
        {
            _signalCalls.Release();
        }
    }

    public async Task<Failure> QueueMutationAsync(MutationIntent intent, CancellationToken cancellationToken)
    {
        if (intent.Account != Account || _mutator is null)
        {
            return Failure.Validation;
        }

        if (!await _operations.QueueAsync(intent, cancellationToken).ConfigureAwait(false))
        {
            return Failure.NeedsReview;
        }

        if (!_policy.AllowNetwork)
        {
            return Failure.None;
        }

        return await ProcessMutationsAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<Failure> ProcessMutationsAsync(CancellationToken cancellationToken)
    {
        if (_mutator is null || !_policy.AllowNetwork)
        {
            return Failure.Unavailable;
        }

        OperationEntry? pending;
        while ((pending = await _operations.ReadNextAsync(Account, cancellationToken).ConfigureAwait(false)) is not null)
        {
            if (pending.State is PendingState.NeedsReview or PendingState.NeedsAuthentication or PendingState.InFlight)
            {
                return pending.State == PendingState.NeedsAuthentication ? Failure.NeedsAuthentication : Failure.NeedsReview;
            }

            Failure result = await _scheduler.EnqueueAsync(
                Account,
                new MutationWork(Account, _operations, _mutator, _time, _retry),
                WorkPriority.Interactive,
                cancellationToken
            ).ConfigureAwait(false);
            if (result != Failure.None)
            {
                return result;
            }
        }

        return Failure.None;
    }

    public async Task<Outcome<DraftSnapshot>> SaveDraftAsync(
        Guid id,
        Stream content,
        CancellationToken cancellationToken
    )
    {
        return await SaveWorkingDraftAsync(id, content, null, cancellationToken).ConfigureAwait(false);
    }

    public Task<Outcome<DraftSnapshot>> SaveDraftRevisionAsync(
        DraftSnapshot expected,
        Stream content,
        CancellationToken cancellationToken
    )
    {
        return expected.Account == Account ? SaveWorkingDraftAsync(expected.Id, content, expected.Revision, cancellationToken) :
            Task.FromResult(Outcome<DraftSnapshot>.Fail(Failure.Validation));
    }

    public async Task<Failure> PushDraftAsync(
        Guid id,
        CancellationToken cancellationToken
    )
    {
        if (_drafts is null || !_policy.AllowNetwork)
        {
            return Failure.Unavailable;
        }
        if (await _draftStore.FinalizeAsync(id, Account, cancellationToken).ConfigureAwait(false) is null)
        {
            return Failure.NotFound;
        }
        return await _scheduler.EnqueueAsync(
            Account,
            new DraftWork(id, Account, _draftStore, _blobs, _drafts, true),
            WorkPriority.Interactive,
            cancellationToken
        ).ConfigureAwait(false);
    }

    public async Task<Outcome<DraftSnapshot>> CloseDraftAsync(
        Guid id,
        bool synchronise,
        CancellationToken cancellationToken
    )
    {
        await _localSaves.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            DraftSnapshot? saved = await _draftStore.FinalizeAsync(id, Account, cancellationToken).ConfigureAwait(false);
            if (saved is null)
            {
                return Outcome<DraftSnapshot>.Fail(Failure.NotFound);
            }
            if (synchronise && _drafts is not null && _policy.AllowNetwork && !saved.Held && !saved.Conflict && saved.FinalizedRevision > saved.PushedRevision)
            {
                lock (_draftUpdates)
                {
                    _draftUpdates.RemoveAll(item => item.Work.IsCompletedSuccessfully || item.Work.IsCanceled);
                    _draftUpdates.Add((id, PushClosedDraftAsync(id)));
                }
            }
            return Outcome<DraftSnapshot>.Success(saved);
        }
        catch (IOException)
        {
            return Outcome<DraftSnapshot>.Fail(Failure.Unavailable);
        }
        finally
        {
            _localSaves.Release();
        }
    }

    public Task WaitForDraftSyncAsync(
        Guid id,
        CancellationToken cancellationToken
    )
    {
        lock (_draftUpdates)
        {
            return Task.WhenAll(_draftUpdates.Where(item => item.Draft == id).Select(item => item.Work)).WaitAsync(cancellationToken);
        }
    }

    public async Task<Failure> DeleteDraftAsync(
        DraftSnapshot draft,
        CancellationToken cancellationToken
    )
    {
        if (draft.Account != Account)
        {
            return Failure.Validation;
        }
        await _localSaves.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            if (!await _draftStore.RequestDeleteAsync(draft, cancellationToken).ConfigureAwait(false))
            {
                return Failure.NeedsReview;
            }
        }
        finally
        {
            _localSaves.Release();
        }
        if (draft.Remote is null)
        {
            return await new DraftDeleteWork(draft.Id, Account, _draftStore, null).RunAsync(cancellationToken).ConfigureAwait(false);
        }
        if (!_policy.AllowNetwork)
        {
            return Failure.None;
        }
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        return await _scheduler.EnqueueAsync(Account, new DraftDeleteWork(draft.Id, Account, _draftStore, _drafts), WorkPriority.Interactive, linked.Token).ConfigureAwait(false);
    }

    public async Task<Failure> SynchroniseDraftsAsync(
        CancellationToken cancellationToken
    )
    {
        if (_drafts is null || _draftStore is not IClientCatalog catalog)
        {
            return Failure.None;
        }
        foreach (DraftSnapshot draft in await catalog.DraftsAsync(Account, cancellationToken).ConfigureAwait(false))
        {
            if (draft.DeletePending)
            {
                Failure deleted = await _scheduler.EnqueueAsync(Account, new DraftDeleteWork(draft.Id, Account, _draftStore, _drafts), WorkPriority.Reconciliation, cancellationToken).ConfigureAwait(false);
                if (deleted != Failure.None)
                {
                    return deleted;
                }
                continue;
            }
            if (draft.Held || draft.Conflict || draft.FinalizedRevision <= draft.PushedRevision)
            {
                continue;
            }
            Failure result = await _scheduler.EnqueueAsync(
                Account,
                new DraftWork(draft.Id, Account, _draftStore, _blobs, _drafts, true),
                WorkPriority.Reconciliation,
                cancellationToken
            ).ConfigureAwait(false);
            if (result != Failure.None)
            {
                return result;
            }
        }
        if (_drafts is IRemoteDraftSource source && _mime is IMimeDraftReader reader)
        {
            string? cursor = null;
            HashSet<string> seen = [];
            do
            {
                DraftImportWork work = new DraftImportWork(Account, source, _draftStore, _blobs, reader, _limits.MaximumMimeBytes, cursor);
                Failure imported = await _scheduler.EnqueueAsync(Account, work, WorkPriority.Reconciliation, cancellationToken).ConfigureAwait(false);
                if (imported != Failure.None)
                {
                    return imported == Failure.Unsupported ? Failure.None : imported;
                }
                cursor = work.Next;
                if (cursor is not null && (!seen.Add(cursor) || seen.Count > 4096))
                {
                    return Failure.Validation;
                }
            }
            while (cursor is not null);
        }
        return Failure.None;
    }

    public async Task<Outcome<DraftReviewSnapshot>> ReviewDraftAsync(
        DraftSnapshot draft,
        CancellationToken cancellationToken
    )
    {
        if (draft.Account != Account || draft.Content.Account != Account || draft.Remote?.Account != Account)
        {
            return Outcome<DraftReviewSnapshot>.Fail(Failure.Validation);
        }
        if (_reader is not IRemoteDraftInspector inspector || _drafts is null || !_policy.AllowNetwork)
        {
            return Outcome<DraftReviewSnapshot>.Fail(Failure.Unavailable);
        }
        DraftReviewWork work = new DraftReviewWork(draft, null, _draftStore, _blobs, _drafts, inspector);
        Failure result = await _scheduler.EnqueueAsync(Account, work, WorkPriority.Interactive, cancellationToken).ConfigureAwait(false);
        return result == Failure.None && work.Review is not null ? Outcome<DraftReviewSnapshot>.Success(work.Review) :
            Outcome<DraftReviewSnapshot>.Fail(result == Failure.None ? Failure.NeedsReview : result);
    }

    public Task<Failure> CopyReviewedDraftAsync(
        DraftReviewSnapshot review,
        CancellationToken cancellationToken
    )
    {
        if (review.Draft.Account != Account || review.Draft.Content.Account != Account ||
            review.Draft.Remote?.Account != Account || review.RemoteContent.Account != Account)
        {
            return Task.FromResult(Failure.Validation);
        }
        if (_reader is not IRemoteDraftInspector inspector || _drafts is null || !_policy.AllowNetwork)
        {
            return Task.FromResult(Failure.Unavailable);
        }
        return _scheduler.EnqueueAsync(
            Account,
            new DraftReviewWork(review.Draft, review, _draftStore, _blobs, _drafts, inspector),
            WorkPriority.Interactive,
            cancellationToken
        );
    }

    public async Task<Outcome<FrozenSubmission>> QueueSubmissionAsync(
        ComposeContent content,
        Guid? sourceDraft,
        Guid? predecessor,
        CancellationToken cancellationToken
    )
    {
        await _localSaves.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            using MemoryStream frozen = new MemoryStream();
            if (!content.Attachments.IsDefaultOrEmpty && content.Attachments.Any(item => item.Content.Account != Account) ||
                sourceDraft is Guid draftId &&
                    await _draftStore.ReadAsync(draftId, Account, cancellationToken).ConfigureAwait(false) is null ||
                predecessor is Guid previous &&
                    await _outbox.ReadAsync(previous, Account, cancellationToken).ConfigureAwait(false) is null)
            {
                return Outcome<FrozenSubmission>.Fail(Failure.Validation);
            }

            Outcome<bool> result = await _mime.FreezeAsync(content, frozen, cancellationToken).ConfigureAwait(false);
            if (!result.IsSuccess)
            {
                return Outcome<FrozenSubmission>.Fail(result.Failure);
            }

            frozen.Position = 0;
            BlobReference blob = await _blobs.WriteAsync(
                Account,
                frozen,
                _limits.MaximumMimeBytes,
                protect: true,
                cancellationToken
            ).ConfigureAwait(false);
            FrozenSubmission submission = new FrozenSubmission(
                Guid.NewGuid(),
                Account,
                blob,
                content.Sender,
                content.Recipients,
                predecessor,
                sourceDraft,
                content.Subject
            );
            bool queued = await _outbox.QueueAsync(submission, cancellationToken).ConfigureAwait(false);
            return queued ? Outcome<FrozenSubmission>.Success(submission) : Outcome<FrozenSubmission>.Fail(Failure.NeedsReview);
        }
        catch (InvalidDataException)
        {
            return Outcome<FrozenSubmission>.Fail(Failure.Validation);
        }
        catch (IOException)
        {
            return Outcome<FrozenSubmission>.Fail(Failure.Unavailable);
        }
        finally
        {
            _localSaves.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stopping = true;
        await _shutdown.CancelAsync().ConfigureAwait(false);
        await _signalCalls.WaitAsync().ConfigureAwait(false);
        _signalCalls.Release();
        await _localSaves.WaitAsync().ConfigureAwait(false);
        Task[] draftUpdates;
        lock (_draftUpdates)
        {
            draftUpdates = _draftUpdates.Select(item => item.Work).ToArray();
        }
        _localSaves.Release();
        await Task.WhenAll(draftUpdates).ConfigureAwait(false);
    }

    public Task<Failure> ProcessOutboxAsync(CancellationToken cancellationToken)
    {
        if (_stopping || !_policy.AllowNetwork)
        {
            return Task.FromResult(Failure.Unavailable);
        }

        return _scheduler.EnqueueAsync(
            Account,
            new OutboxWork(Account, _outbox, _blobs, _submitter),
            WorkPriority.Interactive,
            cancellationToken
        );
    }

    public async Task<Failure> SubmitAsync(Guid submission, CancellationToken cancellationToken)
    {
        if (_stopping || !_policy.AllowNetwork)
        {
            return Failure.Unavailable;
        }

        Failure result = await _scheduler.EnqueueAsync(
            Account,
            new OutboxWork(Account, _outbox, _blobs, _submitter, submission),
            WorkPriority.Interactive,
            cancellationToken
        ).ConfigureAwait(false);
        OutboxEntry? entry = await _outbox.ReadAsync(submission, Account, CancellationToken.None).ConfigureAwait(false);
        if (entry?.State == SubmissionState.Accepted)
        {
            return await StoreSentCopyAsync(submission, cancellationToken).ConfigureAwait(false);
        }

        return entry?.State == SubmissionState.Completed ? Failure.None
            : result != Failure.None ? result : Failure.NeedsReview;
    }

    public Task<Failure> StoreSentCopyAsync(Guid submission, CancellationToken cancellationToken)
    {
        if (_stopping || _sent is null || !_policy.AllowNetwork)
        {
            return Task.FromResult(Failure.Unavailable);
        }

        return _scheduler.EnqueueAsync(
            Account,
            new SentCopyWork(submission, Account, _outbox, _blobs, _sent),
            WorkPriority.Reconciliation,
            cancellationToken
        );
    }

    public Task<Failure> FetchBodyAsync(MessageSnapshot message, CancellationToken cancellationToken)
    {
        return FetchContentAsync(message, false, cancellationToken);
    }

    public Task<Failure> FetchFullMessageAsync(MessageSnapshot message, CancellationToken cancellationToken)
    {
        return FetchContentAsync(message, true, cancellationToken);
    }

    public async Task<Failure> CopyAttachmentAsync(
        MessageSnapshot message,
        string part,
        Stream destination,
        CancellationToken cancellationToken
    )
    {
        if (message.Account != Account || !_policy.AllowNetwork)
        {
            return Failure.Unavailable;
        }

        if (part.StartsWith("imap:", StringComparison.Ordinal) && _content is IMessageAttachmentReader attachments)
        {
            return await attachments.CopyAttachmentAsync(
                message.Provider,
                part,
                destination,
                cancellationToken
            ).ConfigureAwait(false);
        }

        Outcome<Stream> opened = await _content.OpenAsync(message.Provider, cancellationToken).ConfigureAwait(false);
        if (!opened.IsSuccess)
        {
            return opened.Failure;
        }

        await using Stream source = opened.Value!;
        Outcome<bool> copied = await _mime.CopyAttachmentAsync(
            source,
            part,
            destination,
            cancellationToken
        ).ConfigureAwait(false);
        return copied.Failure;
    }

    public Task<Outcome<ImmutableArray<AttachmentSnapshot>>> ReadAttachmentsAsync(MessageSnapshot message, CancellationToken cancellationToken)
    {
        if (message.Account != Account || !_policy.AllowNetwork || _content is not IMessageAttachmentReader attachments)
        {
            return Task.FromResult(Outcome<ImmutableArray<AttachmentSnapshot>>.Fail(Failure.Unavailable));
        }

        return attachments.ReadAttachmentsAsync(message.Provider, cancellationToken);
    }

    private async Task<Outcome<DraftSnapshot>> SaveWorkingDraftAsync(
        Guid id,
        Stream content,
        long? expectedRevision,
        CancellationToken cancellationToken
    )
    {
        await _localSaves.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            if (await _draftStore.ReadAsync(id, Account, cancellationToken).ConfigureAwait(false) is { DeletePending: true })
            {
                return Outcome<DraftSnapshot>.Fail(Failure.NeedsReview);
            }
            BlobReference blob = await _blobs.WriteAsync(
                Account,
                content,
                _limits.MaximumMimeBytes,
                protect: true,
                cancellationToken
            ).ConfigureAwait(false);
            DraftSnapshot saved = expectedRevision is long revision ?
                await _draftStore.SaveRevisionAsync(id, Account, blob, revision, cancellationToken).ConfigureAwait(false) :
                await _draftStore.SaveAsync(id, Account, blob, cancellationToken).ConfigureAwait(false);
            return Outcome<DraftSnapshot>.Success(saved);
        }
        catch (InvalidDataException)
        {
            return Outcome<DraftSnapshot>.Fail(Failure.Validation);
        }
        catch (IOException)
        {
            return Outcome<DraftSnapshot>.Fail(Failure.Unavailable);
        }
        finally
        {
            _localSaves.Release();
        }
    }

    private async Task PushClosedDraftAsync(
        Guid id
    )
    {
        try
        {
            await _scheduler.EnqueueAsync(
                Account,
                new DraftWork(id, Account, _draftStore, _blobs, _drafts!, true),
                WorkPriority.Interactive,
                _shutdown.Token
            ).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
            // The durable closed version or pre-dispatch hold remains the recovery authority.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private Task<Failure> FetchContentAsync(MessageSnapshot message, bool fullMessage, CancellationToken cancellationToken)
    {
        if (message.Account != Account)
        {
            return Task.FromResult(Failure.Validation);
        }

        if (!_policy.AllowNetwork)
        {
            return Task.FromResult(Failure.Unavailable);
        }

        IAccountWork work = new BodyFetchWork(message, _content, _blobs, _mime, _syncStore, _limits.MaximumMimeBytes, fullMessage);
        if (_content is IConcurrentMessageContentReader)
        {
            work = new ConcurrentReadWork(work);
        }
        return _scheduler.EnqueueAsync(
            Account,
            work,
            WorkPriority.Interactive,
            cancellationToken
        );
    }

    private sealed class ConcurrentReadWork(IAccountWork work) : IConcurrentAccountWork
    {
        public Task<Failure> RunAsync(CancellationToken cancellationToken)
        {
            return work.RunAsync(cancellationToken);
        }
    }
}
