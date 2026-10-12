using System;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core;

/// <summary>Returns fully decoded, bounded, metadata-free PNG bytes after explicit consent and destination checks.</summary>
public interface IRemoteImageFetcher
{
    Task<Outcome<byte[]>> FetchAsync(Uri url, bool consent, CancellationToken cancellationToken);
}
