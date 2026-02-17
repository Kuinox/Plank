using K4os.Compression.LZ4;

namespace Plank.Writing;

static class Lz4Compression
{
    internal static void Compress(CompressionContext context, ref BufferWriter source, ref BufferWriter destination)
    {
        var sourceSpan = context.GetContiguousSourceSpan(ref source);
        var maxLength = LZ4Codec.MaximumOutputSize(sourceSpan.Length);
        var destinationSpan = destination.GetSpan(maxLength);
        var written = LZ4Codec.Encode(sourceSpan, destinationSpan, LZ4Level.L00_FAST);
        if (written <= 0)
            throw new InvalidOperationException("LZ4 compression failed.");
        destination.Advance(written);
    }
}
