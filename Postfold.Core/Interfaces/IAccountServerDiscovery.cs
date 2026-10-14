using Postfold.Core.Models.Accounts;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core.Interfaces;

/// <summary>Finds optional server settings without transmitting passwords or creating accounts.</summary>
public interface IAccountServerDiscovery
{
    Task<AccountServerSettings> DiscoverAsync(string email, CancellationToken cancellationToken);
}
