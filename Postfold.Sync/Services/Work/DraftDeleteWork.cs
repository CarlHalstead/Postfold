using Postfold.Core.Enums;
using Postfold.Core.Interfaces;
using Postfold.Core.Models.Accounts;
using Postfold.Core.Models.Drafts;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Sync.Services.Work;

/// <summary>Retries deletion of a stable identity safely; retains local work until remote absence is known.</summary>
public sealed class DraftDeleteWork(Guid id, AccountId account, IDraftStore store, IRemoteDraftStore? remote) : IAccountWork
{
    public async Task<Failure> RunAsync(
        CancellationToken cancellationToken
    )
    {
        DraftSnapshot? draft = await store.ReadAsync(id, account, cancellationToken).ConfigureAwait(false);
        if (draft is null)
        {
            return Failure.None;
        }
        if (!draft.DeletePending)
        {
            return Failure.NeedsReview;
        }
        if (draft.Remote is not null)
        {
            if (remote is not IRemoteDraftDeleter deleter)
            {
                return Failure.Unsupported;
            }
            Failure deleted = await deleter.DeleteDraftAsync(draft.Remote, cancellationToken).ConfigureAwait(false);
            if (deleted != Failure.None)
            {
                return deleted;
            }
        }
        return await store.CompleteDeleteAsync(draft, CancellationToken.None).ConfigureAwait(false) ? Failure.None : Failure.NeedsReview;
    }
}
