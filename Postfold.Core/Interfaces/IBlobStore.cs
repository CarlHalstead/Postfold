using Postfold.Core.Models.Accounts;
using Postfold.Core.Models.Messages;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core.Interfaces;

/// <summary>Streams bounded account-owned content into durable blobs and opens content for caller-owned reading.</summary>
public interface IBlobStore
{
    Task<BlobReference> WriteAsync(
        AccountId account,
        Stream source,
        long maximumBytes,
        bool protect,
        CancellationToken cancellationToken
    );

    /// <summary>Opens a stored blob; the caller owns disposal of the returned stream.</summary>
    Task<Stream> OpenAsync(BlobReference blob, CancellationToken cancellationToken);
}
