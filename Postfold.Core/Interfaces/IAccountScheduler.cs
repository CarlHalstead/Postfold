using Postfold.Core.Enums;
using Postfold.Core.Models.Accounts;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core.Interfaces;

/// <summary>Owns bounded, prioritised account work and participates in asynchronous shutdown.</summary>
public interface IAccountScheduler : IAsyncDisposable
{
    Task<Failure> EnqueueAsync(
        AccountId account,
        IAccountWork work,
        WorkPriority priority,
        CancellationToken cancellationToken
    );
}
