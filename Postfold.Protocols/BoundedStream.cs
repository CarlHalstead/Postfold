using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Protocols;

internal sealed class BoundedStream : Stream
{
    private readonly Stream _inner;
    private readonly long _maximum;
    private long _count;

    internal BoundedStream(Stream inner, long maximum)
    {
        _inner = inner;
        _maximum = maximum;
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => _inner.CanWrite;
    public override long Length => _count;
    public override long Position
    {
        get => _count;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int read = _inner.Read(buffer, offset, count);
        Count(read);
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        Count(read);
        return read;
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        Count(count);
        _inner.Write(buffer, offset, count);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Count(buffer.Length);
        await _inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    public override void Flush()
    {
        _inner.Flush();
    }

    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        return _inner.FlushAsync(cancellationToken);
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotSupportedException();
    }

    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }

    private void Count(int count)
    {
        _count = checked(_count + count);
        if (_count > _maximum)
        {
            throw new InvalidDataException("MIME resource limit exceeded.");
        }
    }
}
