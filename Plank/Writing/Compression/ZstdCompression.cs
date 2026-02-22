using ZstdSharp;

namespace Plank.Writing.Compression;

static class ZstdCompression
{
    internal static void Compress(CompressionContext context, ref BufferWriter source, ref BufferWriter destination)
    {
        var sourceSpan = context.GetContiguousSourceSpan(ref source);
        var compressor = context.GetZstdCompressor();
        compressor.ResetStream();
        compressor.SetPledgedSrcSize((ulong)sourceSpan.Length);
        var maxLength = Compressor.GetCompressBound(sourceSpan.Length);
        var destinationSpan = destination.GetSpan(maxLength);
        var written = compressor.Wrap(sourceSpan, destinationSpan);
        destination.Advance(written);
    }
}
