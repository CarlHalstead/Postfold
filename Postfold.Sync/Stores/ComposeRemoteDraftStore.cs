using Postfold.Core.Enums;
using Postfold.Core.Interfaces;
using Postfold.Core.Models;
using Postfold.Core.Models.Drafts;
using Postfold.Core.Models.Messages;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Sync.Stores;

public sealed class ComposeRemoteDraftStore : IRemoteDraftStore, IRemoteDraftSource, IRemoteDraftDeleter
{
    private readonly IRemoteDraftStore _remote;
    private readonly IMimeDraftWriter _mime;

    public ComposeRemoteDraftStore(IRemoteDraftStore remote, IMimeDraftWriter mime)
    {
        _remote = remote;
        _mime = mime;
    }

    public async Task<RemoteDraftResult> SaveAsync(
        DraftSnapshot draft,
        Stream content,
        CancellationToken cancellationToken
    )
    {
        ComposeContent? compose;
        try
        {
            compose = await JsonSerializer.DeserializeAsync<ComposeContent>(
                content,
                cancellationToken: cancellationToken
            ).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return new RemoteDraftResult(null, null, null, false, Failure.Validation);
        }

        if (compose is null)
        {
            return new RemoteDraftResult(null, null, null, false, Failure.Validation);
        }

        using MemoryStream mime = new MemoryStream();
        Outcome<bool> frozen = await _mime.WriteDraftAsync(compose, mime, cancellationToken).ConfigureAwait(false);
        if (!frozen.IsSuccess)
        {
            return new RemoteDraftResult(null, null, null, false, frozen.Failure);
        }

        mime.Position = 0;
        return await _remote.SaveAsync(draft, mime, cancellationToken).ConfigureAwait(false);
    }

    public Task<Failure> DeleteDraftAsync(
        ProviderIdentity identity,
        CancellationToken cancellationToken
    )
    {
        return _remote is IRemoteDraftDeleter deleter ? deleter.DeleteDraftAsync(identity, cancellationToken) : Task.FromResult(Failure.Unsupported);
    }

    public Task<Outcome<RemoteDraftPage>> ReadDraftPageAsync(
        string? cursor,
        int limit,
        CancellationToken cancellationToken
    )
    {
        return _remote is IRemoteDraftSource source ? source.ReadDraftPageAsync(cursor, limit, cancellationToken) :
            Task.FromResult(Outcome<RemoteDraftPage>.Fail(Failure.Unsupported));
    }
}
