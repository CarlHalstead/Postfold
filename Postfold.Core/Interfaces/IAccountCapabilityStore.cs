using Postfold.Core.Models.Accounts;
using Postfold.Core.Models.Synchronisation;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core.Interfaces;

/// <summary>Persists provider capabilities per account so cached behaviour survives reconnects.</summary>
public interface IAccountCapabilityStore
{
    Task SaveCapabilitiesAsync(AccountId account, Capabilities capabilities, CancellationToken cancellationToken);
}
