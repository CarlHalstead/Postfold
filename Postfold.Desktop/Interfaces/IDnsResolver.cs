using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Desktop.Interfaces;

/// <summary>Resolves destination addresses for the remote-image fetcher's network-boundary checks.</summary>
internal interface IDnsResolver
{
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken);
}
