using Postfold.Core.Enums;
using Postfold.Core.Interfaces;
using Postfold.Core.Models.Drafts;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Sync.Services.Work;

internal sealed class DraftReviewWork(
    DraftSnapshot draft,
    DraftReviewSnapshot? decision,
    IDraftStore store,
    IBlobStore blobs,
    IRemoteDraftStore remote,
    IRemoteDraftInspector inspector
) : IAccountWork
{
    public DraftReviewSnapshot? Review { get; private set; }

    public async Task<Failure> RunAsync(
        CancellationToken cancellationToken
    )
    {
        if (await store.ReadAsync(draft.Id, draft.Account, cancellationToken).ConfigureAwait(false) != draft ||
            draft.Remote is null || !(draft.Held || draft.Conflict))
        {
            return Failure.NeedsReview;
        }
        RemoteDraftResult observed = await inspector.InspectAsync(draft, cancellationToken).ConfigureAwait(false);
        if (observed.Failure != Failure.None || observed.Identity != draft.Remote ||
            observed.Version is null || observed.ConflictingContent is null || observed.Uncertain)
        {
            return observed.Failure == Failure.None ? Failure.NeedsReview : observed.Failure;
        }
        if (decision is null || observed.Version != decision.RemoteVersion)
        {
            DraftReviewSnapshot review = new DraftReviewSnapshot(draft, observed.Version, observed.ConflictingContent, Guid.NewGuid());
            if (!await store.RetainReviewAsync(review, cancellationToken).ConfigureAwait(false))
            {
                return Failure.NeedsReview;
            }
            Review = review;
            return decision is null ? Failure.None : Failure.NeedsReview;
        }
        DraftSnapshot? copy = await store.CopyReviewedAsync(decision, cancellationToken).ConfigureAwait(false);
        if (copy is null)
        {
            return Failure.NeedsReview;
        }
        return await new DraftWork(copy.Id, copy.Account, store, blobs, remote)
            .SaveHeldAsync(copy, cancellationToken).ConfigureAwait(false);
    }
}
