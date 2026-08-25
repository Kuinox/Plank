namespace Plank.Writing;

internal sealed class CompressionContext : IDisposable
{
    readonly BufferWriterFactory _bufferWriters;
    ParquetBuffer _sourceScratch;
    ParquetBuffer _gzipOutputBuffer;

    internal CompressionContext(BufferWriterFactory bufferWriters)
        => _bufferWriters = bufferWriters;

    internal ReadOnlySpan<byte> GetContiguousSourceSpan(ref BufferWriter source)
    {
        if (source.TryGetSingleWrittenSpan(out var span))
            return span;

        var scratch = EnsureSourceScratch(source.WrittenLength);
        source.CopyTo(scratch);
        return scratch;
    }

    internal Span<byte> GetGzipOutputBuffer(int minimumLength)
    {
        if (_gzipOutputBuffer.Length < minimumLength)
        {
            var replacement = _bufferWriters.RentScratch(checked((uint)minimumLength));
            _gzipOutputBuffer.Dispose();
            _gzipOutputBuffer = replacement;
        }

        return _gzipOutputBuffer.Span;
    }

    Span<byte> EnsureSourceScratch(int minimumLength)
    {
        if (_sourceScratch.Length < minimumLength)
        {
            var replacement = _bufferWriters.RentScratch(checked((uint)minimumLength));
            _sourceScratch.Dispose();
            _sourceScratch = replacement;
        }

        return _sourceScratch.Span[..minimumLength];
    }

    public void Dispose()
    {
        _sourceScratch.Dispose();
        _gzipOutputBuffer.Dispose();
    }
}
