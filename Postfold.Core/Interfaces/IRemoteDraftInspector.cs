using Postfold.Core.Models.Drafts;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core.Interfaces;

/// <summary>Reads and retains a remote draft without replacing, deleting or submitting it.</summary>
public interface IRemoteDraftInspector
{
    Task<RemoteDraftResult> InspectAsync(DraftSnapshot draft, CancellationToken cancellationToken);
}
