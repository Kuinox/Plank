using ZstdSharp;
using Plank.Writing.Compression;

namespace Plank.Writing;

internal sealed class CompressionContext : IDisposable
{
    readonly BufferWriterFactory _bufferWriters;
    ParquetBuffer _sourceScratch;
    ParquetBuffer _gzipOutputBuffer;
    GzipDeflater? _gzipDeflater;
    Compressor? _zstdCompressor;

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

    internal GzipDeflater GetGzipDeflater()
        => _gzipDeflater ??= new GzipDeflater();

    internal Compressor GetZstdCompressor()
        => _zstdCompressor ??= new Compressor(1);

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
