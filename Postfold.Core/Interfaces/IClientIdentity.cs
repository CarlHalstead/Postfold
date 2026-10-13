using Postfold.Core.Enums;
using Postfold.Core.Models.Accounts;
using Postfold.Core.Models.Drafts;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core.Interfaces;

/// <summary>Validates and saves the account identity used for composing messages.</summary>
public interface IClientIdentity
{
    Task<Failure> SaveAsync(AccountId account, ComposeIdentity identity, CancellationToken cancellationToken);
}
