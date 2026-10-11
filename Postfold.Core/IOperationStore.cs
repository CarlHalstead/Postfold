using System;
using System.Collections.Immutable;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core;

public interface IOperationStore
{
    Task<bool> QueueAsync(MutationIntent intent, CancellationToken cancellationToken);
    Task<OperationEntry?> ReadNextAsync(AccountId account, CancellationToken cancellationToken);
    Task<OperationEntry?> ClaimAsync(AccountId account, DateTimeOffset now, CancellationToken cancellationToken);
    Task<bool> FinishAsync(OperationEntry claim, PendingState state, Failure evidence, DateTimeOffset due,
        CancellationToken cancellationToken);
    Task RecoverAsync(CancellationToken cancellationToken);
    Task<bool> ResolveAsync(OperationId id, AccountId account, bool retry, CancellationToken cancellationToken);
}
