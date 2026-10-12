using Postfold.Core.Enums;
using Postfold.Core.Models.Accounts;
using Postfold.Core.Models.Operations;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core.Interfaces;

/// <summary>Persists mutation intents and local projections, with revision-aware claims, completion and recovery.</summary>
public interface IOperationStore
{
    Task<bool> QueueAsync(MutationIntent intent, CancellationToken cancellationToken);

    Task<OperationEntry?> ReadNextAsync(AccountId account, CancellationToken cancellationToken);

    Task<OperationEntry?> ClaimAsync(AccountId account, DateTimeOffset now, CancellationToken cancellationToken);

    Task<bool> FinishAsync(
        OperationEntry claim,
        PendingState state,
        Failure evidence,
        DateTimeOffset due,
        CancellationToken cancellationToken
    );

    Task RecoverAsync(CancellationToken cancellationToken);

    Task<bool> ResolveAsync(OperationId id, AccountId account, bool retry, CancellationToken cancellationToken);

    Task<bool> ResolveAsync(
        OperationId id,
        AccountId account,
        long revision,
        bool retry,
        CancellationToken cancellationToken
    );
}
