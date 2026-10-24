using Postfold.Core.Models;
using Postfold.Core.Models.Drafts;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core.Interfaces;

/// <summary>Reads bounded remote draft pages for the shared draft synchronization path.</summary>
public interface IRemoteDraftSource
{
    Task<Outcome<RemoteDraftPage>> ReadDraftPageAsync(string? cursor, int limit, CancellationToken cancellationToken);
}
