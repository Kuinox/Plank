using Plank.Snappy;

namespace Plank.Writing;

static class SnappyCompression
{
    internal static void Compress(CompressionContext context, ref BufferWriter source, ref BufferWriter destination)
    {
        var sourceSpan = context.GetContiguousSourceSpan(ref source);
        var maxLength = SnappyCodec.GetMaxCompressedLength(sourceSpan.Length);
        var destinationSpan = destination.GetSpan(maxLength);
        var written = SnappyCodec.Compress(sourceSpan, destinationSpan);
        destination.Advance(written);
    }
}
