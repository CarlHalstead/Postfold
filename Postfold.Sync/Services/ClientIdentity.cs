using Postfold.Core.Enums;
using Postfold.Core.Interfaces;
using Postfold.Core.Models.Accounts;
using Postfold.Core.Models.Drafts;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Sync.Services;

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
