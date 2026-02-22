using System.IO.Compression;

namespace Plank.Writing.Compression;

static class BrotliCompression
{
    internal static void Compress(CompressionContext context, ref BufferWriter source, ref BufferWriter destination)
    {
        var sourceSpan = context.GetContiguousSourceSpan(ref source);
        var maxLength = BrotliEncoder.GetMaxCompressedLength(sourceSpan.Length);
        var destinationSpan = destination.GetSpan(maxLength);
        if (!BrotliEncoder.TryCompress(sourceSpan, destinationSpan, out var written))
            throw new InvalidOperationException("Brotli compression failed.");
        destination.Advance(written);
    }
}
