using Postfold.Core.Models.Accounts;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core.Interfaces;

/// <summary>Evicts disposable account content to a budget while preserving drafts, outbox and unresolved work.</summary>
public interface ICacheStore
{
    Task<long> EvictAsync(AccountId account, long budgetBytes, CancellationToken cancellationToken);
}
