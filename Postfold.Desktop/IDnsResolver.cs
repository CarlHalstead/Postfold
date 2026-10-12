using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Desktop;

internal interface IDnsResolver
{
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken);
}
