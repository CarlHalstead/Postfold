using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core;

public interface ICanonicalMailboxReader
{
    AccountId Account
    {
        get;
    }
    Task<Outcome<CanonicalPage>> ReadAsync(Checkpoint? checkpoint, int limit, CancellationToken cancellationToken);
}
