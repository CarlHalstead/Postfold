using Postfold.Core.Enums;
using Postfold.Core.Models.Accounts;
using Postfold.Core.Models.Mailboxes;
using Postfold.Core.Models.Messages;
using Postfold.Core.Models.Mime;
using Postfold.Core.Models.Synchronisation;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core.Interfaces;

/// <summary>Atomically persists metadata and checkpoints, and records locally cached bodies and attachments.</summary>
public interface ISynchronisationStore
{
    Task<Checkpoint?> ReadCheckpointAsync(AccountId account, string scope, CancellationToken cancellationToken);

    /// <summary>Commits a mailbox projection and its checkpoint together, without a network operation in the transaction.</summary>
    Task CommitAsync(MailboxSnapshot mailbox, MetadataPage page, CancellationToken cancellationToken);

    /// <summary>Commits canonical account metadata, memberships and the corresponding checkpoint atomically.</summary>
    Task CommitAccountAsync(CanonicalPage page, CancellationToken cancellationToken);

    Task StoreBodyAsync(
        AccountId account,
        MessageId message,
        BlobReference blob,
        MimeSnapshot content,
        CancellationToken cancellationToken
    );

    Task<Failure> StoreAttachmentsAsync(
        MessageSnapshot message,
        ImmutableArray<AttachmentSnapshot> attachments,
        CancellationToken cancellationToken
    );
}
