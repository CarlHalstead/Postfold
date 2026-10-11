using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Postfold.Core;

namespace Postfold.Protocols;

internal sealed class RemoteImapDraftStore : IRemoteDraftStore
{
    private readonly IRemoteDraftTransport _transport;
    private readonly IBlobStore _blobs;
    private readonly string _mailbox;

    internal RemoteImapDraftStore(IRemoteDraftTransport transport, IBlobStore blobs, string mailbox)
    {
        _transport = transport;
        _blobs = blobs;
        _mailbox = mailbox;
    }

    public Task<RemoteDraftResult> SaveAsync(DraftSnapshot draft, Stream content, CancellationToken cancellationToken)
    {
        return _transport.SaveDraftAsync(_mailbox, _blobs, draft, content, cancellationToken);
    }
}
