using System;
using System.Collections.Immutable;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core;

public interface IDraftStore
{
    Task<DraftSnapshot> SaveAsync(Guid id, AccountId account, BlobReference content, CancellationToken cancellationToken);
    Task<DraftSnapshot?> ReadAsync(Guid id, AccountId account, CancellationToken cancellationToken);
    Task<bool> AcknowledgeAsync(DraftSnapshot transmitted, RemoteDraftResult result, CancellationToken cancellationToken);
}
