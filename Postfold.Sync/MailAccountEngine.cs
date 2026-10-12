using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Postfold.Core;

namespace Postfold.Sync;

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
    private readonly IMailboxSignal? _signal;
    private bool _stopping;

    public MailAccountEngine(IMailboxReader reader, IMessageContentReader content, IMailboxMutator? mutator,
        IRemoteDraftStore? drafts, ISentCopyStore? sent, IMessageSubmitter submitter,
        ISynchronisationStore syncStore, IOperationStore operations, IDraftStore draftStore, IOutboxStore outbox,
        ILocalMailQuery query, IBlobStore blobs, IMimePipeline mime, IAccountScheduler scheduler,
        IWorkPolicy policy, IRetryDelayPolicy retry, TimeProvider time, EngineLimits limits, ICacheStore cache,
        ImmutableArray<MailboxSnapshot> mailboxes, IMailboxSignal? signal = null)
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

    public AccountId Account
    {
        get;
    }
    public ImmutableArray<MailboxSnapshot> Mailboxes
    {
        get;
    }
    public Capabilities Capabilities => _reader.Capabilities;

    public async Task<Failure> ReconcileAsync(string mailbox, CancellationToken cancellationToken)
    {
        if (!_policy.AllowNetwork)
        {
            return Failure.Unavailable;
        }

        MailboxSnapshot? selected = Mailboxes.SingleOrDefault(item => item.RemoteKey == mailbox && item.Selectable);
        if (selected is null)
        {
            return Failure.Validation;
        }

        ReconciliationWork work = new ReconciliationWork(_reader, _syncStore, selected, _limits.MetadataBatch);
        Checkpoint? checkpoint;
        do
        {
            Failure result = await _scheduler.EnqueueAsync(Account, work, WorkPriority.Reconciliation,
                cancellationToken).ConfigureAwait(false);
            if (result != Failure.None)
            {
                return result;
            }

            checkpoint = await _syncStore.ReadCheckpointAsync(Account, mailbox, cancellationToken).ConfigureAwait(false);
        }
        while (checkpoint is { Complete: false });

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
            Outcome<SearchPage> recent = await _query.SearchAsync(new SearchRequest(Account, false, "", null,
                false, Math.Min(200, _limits.PrefetchBatch)), cancellationToken).ConfigureAwait(false);
            foreach (MessageSnapshot message in recent.Value!.Messages)
            {
                if (message.Availability != ContentAvailability.Available &&
                    message.Date >= _time.GetUtcNow().AddDays(-_limits.PrefetchDays) &&
                    message.Provider.Imap?.Mailbox == mailbox)
                {
                    await _scheduler.EnqueueAsync(Account,
                        new BodyFetchWork(message, _content, _blobs, _mime, _syncStore, _limits.MaximumMimeBytes),
                        WorkPriority.Prefetch, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        await _cache.EvictAsync(Account, _limits.CacheBudgetBytes, cancellationToken).ConfigureAwait(false);
        return Failure.None;
    }

    public async Task<Failure> WaitForChangesAsync(string mailbox, TimeSpan maximumWait,
        CancellationToken cancellationToken)
    {
        if (_signal is null || !_policy.AllowNetwork)
        {
            return Failure.Unavailable;
        }

        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _shutdown.Token);
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

            Failure result = await _scheduler.EnqueueAsync(Account,
                new MutationWork(Account, _operations, _mutator, _time, _retry), WorkPriority.Interactive,
                cancellationToken).ConfigureAwait(false);
            if (result != Failure.None)
            {
                return result;
            }
        }

        return Failure.None;
    }

    public async Task<Outcome<DraftSnapshot>> SaveDraftAsync(Guid id, Stream content, CancellationToken cancellationToken)
    {
        await _localSaves.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            BlobReference blob = await _blobs.WriteAsync(Account, content, _limits.MaximumMimeBytes,
                protect: true, cancellationToken).ConfigureAwait(false);
            DraftSnapshot saved = await _draftStore.SaveAsync(id, Account, blob, cancellationToken).ConfigureAwait(false);
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

    public Task<Failure> PushDraftAsync(Guid id, CancellationToken cancellationToken)
    {
        if (_drafts is null || !_policy.AllowNetwork)
        {
            return Task.FromResult(Failure.Unavailable);
        }

        return _scheduler.EnqueueAsync(Account, new DraftWork(id, Account, _draftStore, _blobs, _drafts),
            WorkPriority.Interactive, cancellationToken);
    }

    public async Task<Outcome<FrozenSubmission>> QueueSubmissionAsync(ComposeContent content, Guid? sourceDraft,
        Guid? predecessor,
        CancellationToken cancellationToken)
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
            BlobReference blob = await _blobs.WriteAsync(Account, frozen, _limits.MaximumMimeBytes,
                protect: true, cancellationToken).ConfigureAwait(false);
            FrozenSubmission submission = new FrozenSubmission(Guid.NewGuid(), Account, blob, content.Sender,
                content.Recipients, predecessor, sourceDraft);
            await _outbox.QueueAsync(submission, cancellationToken).ConfigureAwait(false);
            return Outcome<FrozenSubmission>.Success(submission);
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
        _localSaves.Release();
    }

    public Task<Failure> ProcessOutboxAsync(CancellationToken cancellationToken)
    {
        if (!_policy.AllowNetwork)
        {
            return Task.FromResult(Failure.Unavailable);
        }

        return _scheduler.EnqueueAsync(Account, new OutboxWork(Account, _outbox, _blobs, _submitter),
            WorkPriority.Interactive, cancellationToken);
    }

    public Task<Failure> StoreSentCopyAsync(Guid submission, CancellationToken cancellationToken)
    {
        if (_sent is null || !_policy.AllowNetwork)
        {
            return Task.FromResult(Failure.Unavailable);
        }

        return _scheduler.EnqueueAsync(Account, new SentCopyWork(submission, Account, _outbox, _blobs, _sent),
            WorkPriority.Reconciliation, cancellationToken);
    }

    public Task<Failure> FetchBodyAsync(MessageSnapshot message, CancellationToken cancellationToken)
    {
        return FetchContentAsync(message, false, cancellationToken);
    }

    public Task<Failure> FetchFullMessageAsync(MessageSnapshot message, CancellationToken cancellationToken)
    {
        return FetchContentAsync(message, true, cancellationToken);
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

        return _scheduler.EnqueueAsync(Account,
            new BodyFetchWork(message, _content, _blobs, _mime, _syncStore, _limits.MaximumMimeBytes, fullMessage),
            WorkPriority.Interactive, cancellationToken);
    }

    public async Task<Failure> CopyAttachmentAsync(MessageSnapshot message, string part, Stream destination,
        CancellationToken cancellationToken)
    {
        if (message.Account != Account || !_policy.AllowNetwork)
        {
            return Failure.Unavailable;
        }

        if (part.StartsWith("imap:", StringComparison.Ordinal) && _content is IMessageAttachmentReader attachments)
        {
            return await attachments.CopyAttachmentAsync(message.Provider, part, destination,
                cancellationToken).ConfigureAwait(false);
        }

        Outcome<Stream> opened = await _content.OpenAsync(message.Provider, cancellationToken).ConfigureAwait(false);
        if (!opened.IsSuccess)
        {
            return opened.Failure;
        }

        await using Stream source = opened.Value!;
        Outcome<bool> copied = await _mime.CopyAttachmentAsync(source, part, destination,
            cancellationToken).ConfigureAwait(false);
        return copied.Failure;
    }

    public Task<Outcome<ImmutableArray<AttachmentSnapshot>>> ReadAttachmentsAsync(MessageSnapshot message,
        CancellationToken cancellationToken)
    {
        if (message.Account != Account || !_policy.AllowNetwork || _content is not IMessageAttachmentReader attachments)
        {
            return Task.FromResult(Outcome<ImmutableArray<AttachmentSnapshot>>.Fail(Failure.Unavailable));
        }

        return attachments.ReadAttachmentsAsync(message.Provider, cancellationToken);
    }
}
