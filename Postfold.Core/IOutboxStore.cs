using System;
using System.Collections.Immutable;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core;

public interface IOutboxStore
{
    Task QueueAsync(FrozenSubmission submission, CancellationToken cancellationToken);
    Task<OutboxEntry?> ClaimAsync(AccountId account, CancellationToken cancellationToken);
    Task<OutboxEntry?> ClaimSentCopyAsync(Guid id, AccountId account, CancellationToken cancellationToken);
    Task<bool> FinishAsync(OutboxEntry claim, SubmissionOutcome outcome, CancellationToken cancellationToken);
    Task<OutboxEntry?> ReadAsync(Guid id, AccountId account, CancellationToken cancellationToken);
    Task<bool> CompleteSentCopyAsync(Guid id, AccountId account, long revision, CancellationToken cancellationToken);
    Task RecoverAsync(CancellationToken cancellationToken);
    Task<bool> CancelQueuedAsync(Guid id, AccountId account, long revision, CancellationToken cancellationToken);
    Task<bool> PrepareSentCopyRetryAsync(Guid id, AccountId account, long revision, CancellationToken cancellationToken);
}
