using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core;

public interface IAccountIdentityStore
{
    Task<Failure> SaveIdentityAsync(AccountId account, ComposeIdentity identity, CancellationToken cancellationToken);
}
