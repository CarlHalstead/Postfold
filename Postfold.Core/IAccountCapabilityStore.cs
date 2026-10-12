using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core;

public interface IAccountCapabilityStore
{
    Task SaveCapabilitiesAsync(AccountId account, Capabilities capabilities, CancellationToken cancellationToken);
}
