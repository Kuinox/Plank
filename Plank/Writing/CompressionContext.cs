using ZstdSharp;
using Plank.Writing.Compression;

namespace Plank.Writing;

internal sealed class CompressionContext
{
    readonly BufferWriterFactory _bufferWriters;
    byte[]? _sourceScratch;
    byte[]? _gzipOutputBuffer;
    GzipDeflater? _gzipDeflater;
    Compressor? _zstdCompressor;

    internal CompressionContext(BufferWriterFactory bufferWriters)
        => _bufferWriters = bufferWriters;

    internal ReadOnlySpan<byte> GetContiguousSourceSpan(ref BufferWriter source)
    {
        if (source.TryGetSingleWrittenSpan(out var span))
            return span;

        var scratch = EnsureSourceScratch(source.WrittenLength);
        source.CopyTo(scratch.AsSpan(0, source.WrittenLength));
        return scratch.AsSpan(0, source.WrittenLength);
    }

    internal byte[] GetGzipOutputBuffer(int minimumLength)
    {
        if (_gzipOutputBuffer is null || _gzipOutputBuffer.Length < minimumLength)
        {
            var replacement = _bufferWriters.RentScratch(checked((uint)minimumLength));
            if (_gzipOutputBuffer is not null)
                _bufferWriters.ReturnScratch(_gzipOutputBuffer);
            _gzipOutputBuffer = replacement;
        }

        return _gzipOutputBuffer;
    }

    internal GzipDeflater GetGzipDeflater()
        => _gzipDeflater ??= new GzipDeflater();

    internal Compressor GetZstdCompressor()
        => _zstdCompressor ??= new Compressor(1);

    byte[] EnsureSourceScratch(int minimumLength)
    {
        if (_sourceScratch is null || _sourceScratch.Length < minimumLength)
        {
            var replacement = _bufferWriters.RentScratch(checked((uint)minimumLength));
            if (_sourceScratch is not null)
                _bufferWriters.ReturnScratch(_sourceScratch);
            _sourceScratch = replacement;
        }

        return _sourceScratch;
    }
}
