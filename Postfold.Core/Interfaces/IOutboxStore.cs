using Postfold.Core.Models.Accounts;
using Postfold.Core.Models.Outbox;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core.Interfaces;

/// <summary>Persists frozen submissions and revision-aware transitions, preserving uncertainty after interrupted submission.</summary>
public interface IOutboxStore
{
    Task QueueAsync(FrozenSubmission submission, CancellationToken cancellationToken);

    Task<OutboxEntry?> ClaimAsync(AccountId account, CancellationToken cancellationToken);

    Task<OutboxEntry?> ClaimSentCopyAsync(Guid id, AccountId account, CancellationToken cancellationToken);

    Task<bool> FinishAsync(OutboxEntry claim, SubmissionOutcome outcome, CancellationToken cancellationToken);

    Task<OutboxEntry?> ReadAsync(Guid id, AccountId account, CancellationToken cancellationToken);

    Task<bool> CompleteSentCopyAsync(Guid id, AccountId account, long revision, CancellationToken cancellationToken);

    /// <summary>Recovers interrupted claims while retaining uncertain submissions for reconciliation or review.</summary>
    Task RecoverAsync(CancellationToken cancellationToken);

    Task<bool> CancelQueuedAsync(Guid id, AccountId account, long revision, CancellationToken cancellationToken);

    Task<bool> PrepareSentCopyRetryAsync(Guid id, AccountId account, long revision, CancellationToken cancellationToken);
}
