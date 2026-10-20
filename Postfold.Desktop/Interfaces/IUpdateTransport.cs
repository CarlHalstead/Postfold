using Postfold.Core.Enums;
using Postfold.Core.Models;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Desktop.Interfaces;

/// <summary>Bounded delivered artifact representations from one immutable release location.</summary>
internal interface IUpdateTransport
{
    Task<Outcome<byte[]>> FetchAsync(string name, int maximumBytes, CancellationToken cancellationToken);

    /// <summary>Transfers an untrusted distribution envelope under the bulk deadline and actual byte ceiling.</summary>
    Task<Failure> DownloadBundleAsync(string name, long maximumBytes, Stream destination, CancellationToken cancellationToken);
}
