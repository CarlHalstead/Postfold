using Postfold.Core.Enums;
using Postfold.Core.Interfaces;
using Postfold.Core.Models.Accounts;
using Postfold.Core.Models.Outbox;
using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Sync.Services.Work;

public sealed class OutboxWork : IAccountWork
{
    private readonly AccountId _account;
    private readonly IOutboxStore _store;
    private readonly IBlobStore _blobs;
    private readonly IMessageSubmitter _submitter;

    public OutboxWork(AccountId account, IOutboxStore store, IBlobStore blobs, IMessageSubmitter submitter)
    {
        _account = account;
        _store = store;
        _blobs = blobs;
        _submitter = submitter;
    }

    public async Task<Failure> RunAsync(CancellationToken cancellationToken)
    {
        OutboxEntry? claim = await _store.ClaimAsync(_account, cancellationToken).ConfigureAwait(false);
        if (claim is null)
        {
            return Failure.None;
        }

        SubmissionOutcome result;
        try
        {
            await using Stream content = await _blobs.OpenAsync(claim.Submission.Mime, cancellationToken)
                .ConfigureAwait(false);
            result = await _submitter.SubmitAsync(claim.Submission, content, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException)
        {
            result = new SubmissionOutcome(
                SubmissionState.Uncertain,
                SubmissionPhase.DataStarted,
                claim.Submission.Recipients.Select(address =>
                    new RecipientEvidence(address, RecipientState.Uncertain, null)).ToImmutableArray(),
                Failure.NeedsReview
            );
        }

        await _store.FinishAsync(claim, result, CancellationToken.None).ConfigureAwait(false);
        return result.Failure;
    }
}
