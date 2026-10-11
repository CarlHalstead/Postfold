using System;
using System.Collections.Immutable;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core;

public interface ISynchronisationStore
{
    Task<Checkpoint?> ReadCheckpointAsync(AccountId account, string scope, CancellationToken cancellationToken);
    Task CommitAsync(MailboxSnapshot mailbox, MetadataPage page, CancellationToken cancellationToken);
    Task CommitAccountAsync(CanonicalPage page, CancellationToken cancellationToken);
    Task StoreBodyAsync(AccountId account, MessageId message, BlobReference blob, MimeSnapshot content,
        CancellationToken cancellationToken);
}
