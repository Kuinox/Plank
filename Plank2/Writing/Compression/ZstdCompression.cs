using ZstdSharp;

namespace Plank2.Writing;

static class ZstdCompression
{
    internal static void Compress(ref BufferWriter source, ref BufferWriter destination)
    {
        using var output = new BufferWriterOutputStream(destination);
        using (var zstd = new CompressionStream(output, 1, 0, leaveOpen: true))
            source.WriteTo(zstd);
        destination = output.Consume();
    }
}
