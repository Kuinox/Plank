namespace Plank.Writing;

static class Compression
{
    internal static void Compress(CompressionKind compression, CompressionContext context, ref BufferWriter source,
        ref BufferWriter destination)
    {
        destination.Reset();
        switch (compression)
        {
            case CompressionKind.None:
            {
                var destinationSpan = destination.GetSpan(source.WrittenLength);
                source.CopyTo(destinationSpan);
                destination.Advance(source.WrittenLength);
                return;
            }
            case CompressionKind.Snappy:
                SnappyCompression.Compress(context, ref source, ref destination);
                return;
            case CompressionKind.Gzip:
                GzipCompression.Compress(context, ref source, ref destination);
                return;
            case CompressionKind.Zstd:
                ZstdCompression.Compress(context, ref source, ref destination);
                return;
            case CompressionKind.Lz4:
                Lz4Compression.Compress(context, ref source, ref destination);
                return;
            case CompressionKind.Brotli:
                BrotliCompression.Compress(context, ref source, ref destination);
                return;
            default:
                throw new NotSupportedException($"Compression '{compression}' is not supported.");
        }
    }
}
