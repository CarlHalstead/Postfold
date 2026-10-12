using System;
using System.Collections.Immutable;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core;

public interface ILocalMailQuery
{
    Task<Outcome<SearchPage>> SearchAsync(SearchRequest request, CancellationToken cancellationToken);
    Task<Outcome<BlobReference>> ReadBodyAsync(AccountId account, MessageId message, CancellationToken cancellationToken);
    Task<ImmutableArray<AttachmentSnapshot>> ReadAttachmentsAsync(AccountId account, MessageId message,
        CancellationToken cancellationToken);
}
