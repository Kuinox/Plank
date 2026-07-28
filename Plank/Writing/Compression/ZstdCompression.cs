using ZstdSharp.Unsafe;

namespace Plank.Writing.Compression;

unsafe static class ZstdCompression
{
    internal static void Compress(int compressionLevel, CompressionContext context, ref BufferWriter source,
        ref BufferWriter destination)
    {
        var sourceSpan = context.GetContiguousSourceSpan(ref source);
        var maxLength = checked((int)Methods.ZSTD_compressBound((nuint)sourceSpan.Length));
        var destinationSpan = destination.GetSpan(maxLength);
        fixed (byte* input = sourceSpan)
        fixed (byte* output = destinationSpan)
        {
            var written = Methods.ZSTD_compress(output, (nuint)destinationSpan.Length, input,
                (nuint)sourceSpan.Length, compressionLevel);
            if (Methods.ZSTD_isError(written))
                throw new InvalidOperationException($"Zstd compression failed with code {written}.");
            destination.Advance(checked((int)written));
        }
    }
}
