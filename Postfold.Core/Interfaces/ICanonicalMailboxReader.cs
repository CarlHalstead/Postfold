using Postfold.Core.Models;
using Postfold.Core.Models.Accounts;
using Postfold.Core.Models.Synchronisation;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core.Interfaces;

/// <summary>Reads account-wide canonical messages and memberships using provider checkpoints rather than mailbox copies.</summary>
public interface ICanonicalMailboxReader
{
    AccountId Account
    {
        get;
    }

    Task<Outcome<CanonicalPage>> ReadAsync(Checkpoint? checkpoint, int limit, CancellationToken cancellationToken);
}
