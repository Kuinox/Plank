namespace Plank.Writing;

static class GzipCompression
{
    internal static void Compress(CompressionContext context, ref BufferWriter source, ref BufferWriter destination)
    {
        var input = context.GetContiguousSourceSpan(ref source);
        var outputBuffer = context.GetGzipOutputBuffer(64 * 1024);
        var deflater = context.GetGzipDeflater();
        deflater.Compress(input, outputBuffer, ref destination);
    }
}
