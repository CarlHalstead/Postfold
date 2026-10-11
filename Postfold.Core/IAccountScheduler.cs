using System;
using System.Collections.Immutable;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core;

public interface IAccountScheduler : IAsyncDisposable
{
    Task<Failure> EnqueueAsync(AccountId account, IAccountWork work, WorkPriority priority,
        CancellationToken cancellationToken);
}
