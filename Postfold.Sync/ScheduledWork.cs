using System.Threading;
using System.Threading.Tasks;
using Postfold.Core;

namespace Postfold.Sync;

internal sealed record ScheduledWork(AccountId Account, IAccountWork Work, WorkPriority Priority,
    CancellationToken Cancellation, TaskCompletionSource<Failure> Completion);
