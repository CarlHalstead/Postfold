using System.Collections.Immutable;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core;

public interface IMessageAttachmentReader
{
    Task<Outcome<ImmutableArray<AttachmentSnapshot>>> ReadAttachmentsAsync(ProviderIdentity identity,
        CancellationToken cancellationToken);
    Task<Failure> CopyAttachmentAsync(ProviderIdentity identity, string part, Stream destination,
        CancellationToken cancellationToken);
}
