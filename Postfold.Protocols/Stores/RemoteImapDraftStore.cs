using Postfold.Core.Enums;
using Postfold.Core.Interfaces;
using Postfold.Core.Models;
using Postfold.Core.Models.Drafts;
using Postfold.Core.Models.Messages;
using Postfold.Core.Models.Synchronisation;
using Postfold.Protocols.Interfaces;
using System.Collections.Immutable;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Protocols.Stores;

internal sealed class RemoteImapDraftStore : IRemoteDraftStore, IRemoteDraftSource, IRemoteDraftDeleter
{
    private readonly IRemoteDraftTransport _transport;
    private readonly IBlobStore _blobs;
    private readonly string _mailbox;
    private readonly IMailboxReader _reader;
    private readonly IMessageContentReader _content;
    private readonly long _maximumBytes;

    internal RemoteImapDraftStore(
        IRemoteDraftTransport transport,
        IBlobStore blobs,
        string mailbox,
        IMailboxReader reader,
        IMessageContentReader content,
        long maximumBytes
    )
    {
        _transport = transport;
        _blobs = blobs;
        _mailbox = mailbox;
        _reader = reader;
        _content = content;
        _maximumBytes = maximumBytes;
    }

    public Task<Failure> DeleteDraftAsync(
        ProviderIdentity identity,
        CancellationToken cancellationToken
    )
    {
        return _transport.DeleteDraftAsync(_mailbox, identity, cancellationToken);
    }

    public Task<RemoteDraftResult> SaveAsync(DraftSnapshot draft, Stream content, CancellationToken cancellationToken)
    {
        return _transport.SaveDraftAsync(_mailbox, _blobs, draft, content, cancellationToken);
    }

    public async Task<Outcome<RemoteDraftPage>> ReadDraftPageAsync(
        string? cursor,
        int limit,
        CancellationToken cancellationToken
    )
    {
        if (cursor?.Length > 16384 || limit is < 1 or > 500)
        {
            return Outcome<RemoteDraftPage>.Fail(Failure.Validation);
        }
        Checkpoint? checkpoint;
        try
        {
            checkpoint = cursor is null ? null : JsonSerializer.Deserialize<Checkpoint>(cursor);
        }
        catch (JsonException)
        {
            return Outcome<RemoteDraftPage>.Fail(Failure.Validation);
        }
        if (checkpoint is not null && (checkpoint.Account != _reader.Account || checkpoint.Scope != _mailbox))
        {
            return Outcome<RemoteDraftPage>.Fail(Failure.Validation);
        }
        Outcome<MetadataPage> page = await _reader.ReadAsync(_mailbox, checkpoint, limit, cancellationToken).ConfigureAwait(false);
        if (!page.IsSuccess)
        {
            return Outcome<RemoteDraftPage>.Fail(page.Failure);
        }
        ImmutableArray<RemoteDraftResult>.Builder drafts = ImmutableArray.CreateBuilder<RemoteDraftResult>();
        foreach (MessageSnapshot message in page.Value!.Messages)
        {
            Outcome<Stream> opened = await _content.OpenAsync(message.Provider, cancellationToken).ConfigureAwait(false);
            if (!opened.IsSuccess)
            {
                return Outcome<RemoteDraftPage>.Fail(opened.Failure);
            }
            await using Stream mime = opened.Value!;
            BlobReference blob = await _blobs.WriteAsync(_reader.Account, mime, _maximumBytes, true, cancellationToken).ConfigureAwait(false);
            drafts.Add(new RemoteDraftResult(message.Provider, blob.Sha256, blob, false, Failure.None));
        }
        string? next = page.Value.Checkpoint.Complete ? null : JsonSerializer.Serialize(page.Value.Checkpoint);
        return Outcome<RemoteDraftPage>.Success(new RemoteDraftPage(drafts.ToImmutable(), next));
    }
}
