namespace Plank.Writing.Compression;

static class GzipCompression
{
    internal static void Compress(int compressionLevel, CompressionContext context, ref BufferWriter source,
        ref BufferWriter destination)
    {
        var input = context.GetContiguousSourceSpan(ref source);
        var outputBuffer = context.GetGzipOutputBuffer(64 * 1024);
        GzipDeflater.Compress(compressionLevel, input, outputBuffer, ref destination);
    }
}
