namespace BcTelemetryProxy.Ingestion;

/// <summary>Thrown when a wrapped stream exceeds its configured byte budget.</summary>
public sealed class StreamLimitExceededException : Exception
{
    public StreamLimitExceededException(long limit)
        : base($"Stream exceeded the configured limit of {limit} bytes.") => Limit = limit;

    public long Limit { get; }
}

/// <summary>
/// Read-only forward-only wrapper that throws <see cref="StreamLimitExceededException"/>
/// once more than <c>limit</c> bytes have been read. Used to bound both the
/// compressed request body and the decompressed payload so a malicious or
/// runaway request cannot exhaust memory.
/// </summary>
public sealed class LimitingStream : Stream
{
    private readonly Stream _inner;
    private readonly long _limit;
    private long _read;

    public LimitingStream(Stream inner, long limit)
    {
        _inner = inner;
        _limit = limit;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => _read; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var n = _inner.Read(buffer, offset, count);
        Account(n);
        return n;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var n = await _inner.ReadAsync(buffer, cancellationToken);
        Account(n);
        return n;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    private void Account(int n)
    {
        _read += n;
        if (_read > _limit)
        {
            throw new StreamLimitExceededException(_limit);
        }
    }

    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
