using Postfold.Core.Enums;
using Postfold.Core.Interfaces;
using Postfold.Core.Models.Accounts;
using Postfold.Core.Models.Operations;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Sync.Services.Work;

public sealed class MutationWork : IAccountWork
{
    private readonly AccountId _account;
    private readonly IOperationStore _store;
    private readonly IMailboxMutator _mutator;
    private readonly TimeProvider _time;
    private readonly IRetryDelayPolicy _retry;

    public MutationWork(
        AccountId account,
        IOperationStore store,
        IMailboxMutator mutator,
        TimeProvider time,
        IRetryDelayPolicy retry
    )
    {
        _account = account;
        _store = store;
        _mutator = mutator;
        _time = time;
        _retry = retry;
    }

    public async Task<Failure> RunAsync(CancellationToken cancellationToken)
    {
        OperationEntry? pending = await _store.ReadNextAsync(_account, cancellationToken).ConfigureAwait(false);
        if (pending?.State == PendingState.RetryScheduled && pending.Due > _time.GetUtcNow())
        {
            await Task.Delay(pending.Due - _time.GetUtcNow(), _time, cancellationToken).ConfigureAwait(false);
        }

        OperationEntry? claim = await _store.ClaimAsync(_account, _time.GetUtcNow(), cancellationToken)
            .ConfigureAwait(false);
        if (claim is null)
        {
            return Failure.None;
        }

        Failure result;
        try
        {
            result = await _mutator.ApplyAsync(claim.Intent, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            result = Failure.NeedsReview;
        }

        PendingState state = result switch
        {
            Failure.None => PendingState.Completed,
            Failure.Transient => PendingState.RetryScheduled,
            Failure.NeedsAuthentication => PendingState.NeedsAuthentication,
            Failure.NeedsReview or Failure.NotFound => PendingState.NeedsReview,
            _ => PendingState.Failed
        };
        DateTimeOffset due = _time.GetUtcNow() + (result == Failure.Transient ?
            _retry.Delay(claim.Attempts) : TimeSpan.Zero);
        await _store.FinishAsync(claim, state, result, due, CancellationToken.None).ConfigureAwait(false);
        return result;
    }
}
