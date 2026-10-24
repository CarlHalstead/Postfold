using Postfold.Core.Models.Accounts;
using Postfold.Core.Models.Drafts;
using Postfold.Core.Models.Messages;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core.Interfaces;

/// <summary>Persists local draft revisions and conditionally acknowledges the exact revision saved remotely.</summary>
public interface IDraftStore
{
    /// <summary>Queues deletion only for the current snapshot without unresolved submission or unknown remote effects.</summary>
    Task<bool> RequestDeleteAsync(DraftSnapshot draft, CancellationToken cancellationToken);

    /// <summary>Removes a queued draft and its versions after its remote identity is confirmed absent.</summary>
    Task<bool> CompleteDeleteAsync(DraftSnapshot draft, CancellationToken cancellationToken);
    Task<DraftSnapshot> SaveAsync(Guid id, AccountId account, BlobReference content, CancellationToken cancellationToken);

    /// <summary>Preserves edits and marks a conflict when the working revision changed since this editor last observed it.</summary>
    Task<DraftSnapshot> SaveRevisionAsync(Guid id, AccountId account, BlobReference content, long expectedRevision, CancellationToken cancellationToken);

    Task<DraftSnapshot?> ReadAsync(Guid id, AccountId account, CancellationToken cancellationToken);

    /// <summary>Captures the current working draft as one closed version; repeated closure of the same revision is idempotent.</summary>
    Task<DraftSnapshot?> FinalizeAsync(Guid id, AccountId account, CancellationToken cancellationToken);

    /// <summary>Reads the last closed version for remote work, excluding newer in-progress autosaves.</summary>
    Task<DraftSnapshot?> ReadFinalizedAsync(Guid id, AccountId account, CancellationToken cancellationToken);

    /// <summary>Imports a remote snapshot or refreshes a clean bound draft; dirty and uncertain work is retained as a conflict.</summary>
    Task<DraftSnapshot> ImportRemoteAsync(RemoteDraftResult remote, BlobReference content, CancellationToken cancellationToken);

    /// <summary>Retains remote review bytes only while the exact local snapshot remains current.</summary>
    Task<bool> RetainReviewAsync(DraftReviewSnapshot review, CancellationToken cancellationToken);

    /// <summary>Atomically creates one held local copy from a retained, still-current review; originals remain bound and protected.</summary>
    Task<DraftSnapshot?> CopyReviewedAsync(DraftReviewSnapshot review, CancellationToken cancellationToken);

    /// <summary>Acknowledges the transmitted revision only; newer local edits must remain pending.</summary>
    Task<bool> AcknowledgeAsync(DraftSnapshot transmitted, RemoteDraftResult result, CancellationToken cancellationToken);
}
