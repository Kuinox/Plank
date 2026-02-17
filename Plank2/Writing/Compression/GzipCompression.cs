using System.IO.Compression;

namespace Plank2.Writing;

static class GzipCompression
{
    internal static void Compress(ref BufferWriter source, ref BufferWriter destination)
    {
        using var output = new BufferWriterOutputStream(destination);
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
            source.WriteTo(gzip);
        destination = output.Consume();
    }
}
