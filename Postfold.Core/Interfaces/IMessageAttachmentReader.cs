using Postfold.Core.Enums;
using Postfold.Core.Models;
using Postfold.Core.Models.Messages;
using System.Collections.Immutable;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core.Interfaces;

/// <summary>Reads provider attachment metadata and streams a selected part to a caller-owned destination.</summary>
public interface IMessageAttachmentReader
{
    Task<Outcome<ImmutableArray<AttachmentSnapshot>>> ReadAttachmentsAsync(
        ProviderIdentity identity,
        CancellationToken cancellationToken
    );

    Task<Failure> CopyAttachmentAsync(
        ProviderIdentity identity,
        string part,
        Stream destination,
        CancellationToken cancellationToken
    );
}
