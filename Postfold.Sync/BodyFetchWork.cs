using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Postfold.Core;

namespace Postfold.Sync;

public sealed class BodyFetchWork : IAccountWork
{
    private readonly MessageSnapshot _message;
    private readonly IMessageContentReader _reader;
    private readonly IBlobStore _blobs;
    private readonly IMimePipeline _mime;
    private readonly ISynchronisationStore _store;
    private readonly long _maximumBytes;
    private readonly bool _fullMessage;

    public BodyFetchWork(MessageSnapshot message, IMessageContentReader reader, IBlobStore blobs,
        IMimePipeline mime, ISynchronisationStore store, long maximumBytes, bool fullMessage = false)
    {
        _message = message;
        _reader = reader;
        _blobs = blobs;
        _mime = mime;
        _store = store;
        _maximumBytes = maximumBytes;
        _fullMessage = fullMessage;
    }

    public async Task<Failure> RunAsync(CancellationToken cancellationToken)
    {
        Outcome<Stream> result = _fullMessage ?
            await _reader.OpenAsync(_message.Provider, cancellationToken).ConfigureAwait(false) :
            await _reader.OpenBodyAsync(_message.Provider, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return result.Failure;
        }

        await using Stream source = result.Value!;
        BlobReference blob = await _blobs.WriteAsync(_message.Account, source, _maximumBytes,
            protect: false, cancellationToken).ConfigureAwait(false);
        await using Stream cached = await _blobs.OpenAsync(blob, cancellationToken).ConfigureAwait(false);
        Outcome<MimeSnapshot> parsed = await _mime.ParseAsync(cached, cancellationToken).ConfigureAwait(false);
        if (!parsed.IsSuccess)
        {
            return parsed.Failure;
        }

        await _store.StoreBodyAsync(_message.Account, _message.Id, blob, parsed.Value!, cancellationToken)
            .ConfigureAwait(false);
        return Failure.None;
    }
}
