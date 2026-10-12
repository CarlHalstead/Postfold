using Postfold.Desktop.Interfaces;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Desktop.Services;

internal sealed class SystemDnsResolver : IDnsResolver
{
    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        return Dns.GetHostAddressesAsync(host, cancellationToken);
    }
}
