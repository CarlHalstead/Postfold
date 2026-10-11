using System;
using System.Collections.Immutable;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core;

public interface IRemoteDraftStore
{
    Task<RemoteDraftResult> SaveAsync(DraftSnapshot draft, Stream content, CancellationToken cancellationToken);
}
