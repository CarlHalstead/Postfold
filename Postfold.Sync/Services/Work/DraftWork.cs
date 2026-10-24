using Postfold.Core.Enums;
using Postfold.Core.Interfaces;
using Postfold.Core.Models.Accounts;
using Postfold.Core.Models.Drafts;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Sync.Services.Work;

public sealed class DraftWork : IAccountWork
{
    private readonly Guid _id;
    private readonly AccountId _account;
    private readonly IDraftStore _store;
    private readonly IBlobStore _blobs;
    private readonly IRemoteDraftStore _remote;
    private readonly bool _finalizedOnly;

    public DraftWork(
        Guid id,
        AccountId account,
        IDraftStore store,
        IBlobStore blobs,
        IRemoteDraftStore remote,
        bool finalizedOnly = false
    )
    {
        _id = id;
        _account = account;
        _store = store;
        _blobs = blobs;
        _remote = remote;
        _finalizedOnly = finalizedOnly;
    }

    public async Task<Failure> RunAsync(
        CancellationToken cancellationToken
    )
    {
        DraftSnapshot? draft = _finalizedOnly ?
            await _store.ReadFinalizedAsync(_id, _account, cancellationToken).ConfigureAwait(false) :
            await _store.ReadAsync(_id, _account, cancellationToken).ConfigureAwait(false);
        if (draft is null)
        {
            return Failure.NotFound;
        }

        if (draft.DeletePending || draft.Held || draft.Conflict)
        {
            return Failure.NeedsReview;
        }

        if (draft.PushedRevision >= draft.Revision)
        {
            return Failure.None;
        }

        // Persist the hold before any append; a process crash cannot blindly repeat an uncertain draft write.
        await _store.AcknowledgeAsync(
            draft,
            new RemoteDraftResult(null, null, null, true, Failure.NeedsReview),
            cancellationToken
        ).ConfigureAwait(false);
        return await SaveHeldAsync(draft, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<Failure> SaveHeldAsync(
        DraftSnapshot draft,
        CancellationToken cancellationToken
    )
    {
        await using Stream content = await _blobs.OpenAsync(draft.Content, cancellationToken).ConfigureAwait(false);
        RemoteDraftResult result = await _remote.SaveAsync(draft, content, cancellationToken).ConfigureAwait(false);
        await _store.AcknowledgeAsync(draft, result, CancellationToken.None).ConfigureAwait(false);
        return result.Uncertain || result.ConflictingContent is not null ? Failure.NeedsReview : result.Failure;
    }
}
