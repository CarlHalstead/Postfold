using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core;

public interface IClientIdentity
{
    Task<Failure> SaveAsync(AccountId account, ComposeIdentity identity, CancellationToken cancellationToken);
}
