using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Postfold.Core;

namespace Postfold.Protocols;

internal interface IRemoteDraftTransport
{
    Task<RemoteDraftResult> SaveDraftAsync(string mailbox, IBlobStore blobs, DraftSnapshot draft, Stream content,
        CancellationToken cancellationToken);
}
