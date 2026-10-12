using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Postfold.Core;

namespace Postfold.Sync;

public sealed class ComposeRemoteDraftStore : IRemoteDraftStore
{
    private readonly IRemoteDraftStore _remote;
    private readonly IMimeDraftWriter _mime;

    public ComposeRemoteDraftStore(IRemoteDraftStore remote, IMimeDraftWriter mime)
    {
        _remote = remote;
        _mime = mime;
    }

    public async Task<RemoteDraftResult> SaveAsync(DraftSnapshot draft, Stream content,
        CancellationToken cancellationToken)
    {
        ComposeContent? compose;
        try
        {
            compose = await JsonSerializer.DeserializeAsync<ComposeContent>(content,
                cancellationToken: cancellationToken).ConfigureAwait(false);
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
}
