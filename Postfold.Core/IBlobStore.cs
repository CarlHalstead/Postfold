using System;
using System.Collections.Immutable;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core;

public interface IBlobStore
{
    Task<BlobReference> WriteAsync(AccountId account, Stream source, long maximumBytes, bool protect,
        CancellationToken cancellationToken);
    Task<Stream> OpenAsync(BlobReference blob, CancellationToken cancellationToken);
}
