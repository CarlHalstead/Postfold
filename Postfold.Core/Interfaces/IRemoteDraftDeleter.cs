using Postfold.Core.Enums;
using Postfold.Core.Models.Messages;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core.Interfaces;

/// <summary>Idempotently deletes one account-bound remote draft identity; missing identities count as success.</summary>
public interface IRemoteDraftDeleter
{
    Task<Failure> DeleteDraftAsync(ProviderIdentity identity, CancellationToken cancellationToken);
}
