using K4os.Compression.LZ4;

namespace Plank.Writing.Compression;

static class Lz4Compression
{
    internal static void Compress(int compressionLevel, CompressionContext context, ref BufferWriter source,
        ref BufferWriter destination)
    {
        var sourceSpan = context.GetContiguousSourceSpan(ref source);
        var maxLength = LZ4Codec.MaximumOutputSize(sourceSpan.Length);
        var destinationSpan = destination.GetSpan(maxLength);
        var written = LZ4Codec.Encode(sourceSpan, destinationSpan, (LZ4Level)compressionLevel);
        if (written <= 0)
            throw new InvalidOperationException("LZ4 compression failed.");
        destination.Advance(written);
    }
}
