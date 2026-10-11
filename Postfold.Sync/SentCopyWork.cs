using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Postfold.Core;

namespace Postfold.Sync;

public sealed class SentCopyWork : IAccountWork
{
    private readonly Guid _id;
    private readonly AccountId _account;
    private readonly IOutboxStore _store;
    private readonly IBlobStore _blobs;
    private readonly ISentCopyStore _remote;

    public SentCopyWork(Guid id, AccountId account, IOutboxStore store, IBlobStore blobs, ISentCopyStore remote)
    {
        _id = id;
        _account = account;
        _store = store;
        _blobs = blobs;
        _remote = remote;
    }

    public async Task<Failure> RunAsync(CancellationToken cancellationToken)
    {
        OutboxEntry? entry = await _store.ClaimSentCopyAsync(_id, _account, cancellationToken).ConfigureAwait(false);
        if (entry is null)
        {
            return Failure.NeedsReview;
        }

        await using Stream content = await _blobs.OpenAsync(entry.Submission.Mime, cancellationToken).ConfigureAwait(false);
        Failure result = await _remote.SaveAsync(entry.Submission, content, cancellationToken).ConfigureAwait(false);
        if (result == Failure.None)
        {
            await _store.CompleteSentCopyAsync(_id, _account, entry.Revision, CancellationToken.None).ConfigureAwait(false);
        }

        return result;
    }
}
