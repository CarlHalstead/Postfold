using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core;

public interface ICacheStore
{
    Task<long> EvictAsync(AccountId account, long budgetBytes, CancellationToken cancellationToken);
}
