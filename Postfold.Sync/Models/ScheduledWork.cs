using Postfold.Core.Enums;
using Postfold.Core.Interfaces;
using Postfold.Core.Models.Accounts;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Sync.Models;

internal sealed record ScheduledWork(
    AccountId Account,
    IAccountWork Work,
    WorkPriority Priority,
    CancellationToken Cancellation,
    TaskCompletionSource<Failure> Completion
);
