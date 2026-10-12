using Postfold.Core.Models.Drafts;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core.Interfaces;

/// <summary>Saves a local draft snapshot remotely and reports acknowledgement or conflict evidence.</summary>
public interface IRemoteDraftStore
{
    Task<RemoteDraftResult> SaveAsync(DraftSnapshot draft, Stream content, CancellationToken cancellationToken);
}
