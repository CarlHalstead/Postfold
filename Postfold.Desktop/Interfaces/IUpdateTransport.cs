using Postfold.Core.Enums;
using Postfold.Core.Models;
using Postfold.Core.Models.Updates;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Desktop.Interfaces;

/// <summary>Bounded delivered artifact representations from one immutable release location.</summary>
internal interface IUpdateTransport
{
    Task<Outcome<byte[]>> FetchAsync(string name, int maximumBytes, CancellationToken cancellationToken);

    Task<Failure> DownloadAsync(UpdateArtifact artifact, Stream destination, CancellationToken cancellationToken);
}
