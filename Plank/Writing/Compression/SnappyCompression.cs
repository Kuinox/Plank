using Snappier;

namespace Plank.Writing;

static class SnappyCompression
{
    internal static void Compress(BufferWriterFactory bufferWriters, ref BufferWriter source, ref BufferWriter destination)
    {
        var sourceSpan = CompressionSource.AsContiguous(bufferWriters, ref source, out var scratch);
        try
        {
            var maxLength = Snappy.GetMaxCompressedLength(sourceSpan.Length);
            var destinationSpan = destination.GetSpan(maxLength);
            var written = Snappy.Compress(sourceSpan, destinationSpan);
            destination.Advance(written);
        }
        finally
        {
            if (scratch is not null)
                bufferWriters.ReturnScratch(scratch);
        }
    }
}
