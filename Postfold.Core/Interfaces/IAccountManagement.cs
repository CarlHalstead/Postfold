using Postfold.Core.Enums;
using Postfold.Core.Models.Accounts;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core.Interfaces;

/// <summary>Disconnects an account locally without changing remote mail or deleting retained evidence.</summary>
public interface IAccountManagement
{
    /// <summary>Changes only the local display name; preserves addresses, credentials and durable account identity.</summary>
    Task<Failure> RenameAsync(AccountId account, string name, CancellationToken cancellationToken);

    /// <summary>Refuses with NeedsReview while drafts, submissions or mutations remain unresolved.</summary>
    Task<Failure> RemoveAsync(AccountId account, CancellationToken cancellationToken);
}
