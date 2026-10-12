using System.Threading;
using System.Threading.Tasks;
using Postfold.Core;

namespace Postfold.Sync;

public sealed class ClientIdentity : IClientIdentity
{
    private readonly IAccountIdentityStore _store;

    public ClientIdentity(IAccountIdentityStore store)
    {
        _store = store;
    }

    public Task<Failure> SaveAsync(AccountId account, ComposeIdentity identity, CancellationToken cancellationToken)
    {
        return !identity.IsValid ? Task.FromResult(Failure.Validation) :
            _store.SaveIdentityAsync(account, identity, cancellationToken);
    }
}
