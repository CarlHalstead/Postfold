using Postfold.Core.Enums;
using Postfold.Core.Models.Accounts;
using Postfold.Core.Models.Drafts;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core.Interfaces;

/// <summary>Persists an account's compose identity and signature independently of network credentials.</summary>
public interface IAccountIdentityStore
{
    Task<Failure> SaveIdentityAsync(AccountId account, ComposeIdentity identity, CancellationToken cancellationToken);
}
