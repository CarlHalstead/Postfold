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
    Task<DraftSnapshot> SaveAsync(Guid id, AccountId account, BlobReference content, CancellationToken cancellationToken);

    Task<DraftSnapshot?> ReadAsync(Guid id, AccountId account, CancellationToken cancellationToken);

    /// <summary>Acknowledges the transmitted revision only; newer local edits must remain pending.</summary>
    Task<bool> AcknowledgeAsync(DraftSnapshot transmitted, RemoteDraftResult result, CancellationToken cancellationToken);
}
