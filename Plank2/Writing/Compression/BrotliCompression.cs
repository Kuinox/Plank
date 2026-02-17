using System.IO.Compression;

namespace Plank2.Writing;

static class BrotliCompression
{
    internal static void Compress(BufferWriterFactory bufferWriters, ref BufferWriter source, ref BufferWriter destination)
    {
        var sourceSpan = CompressionSource.AsContiguous(bufferWriters, ref source, out var scratch);
        try
        {
            var maxLength = BrotliEncoder.GetMaxCompressedLength(sourceSpan.Length);
            var destinationSpan = destination.GetSpan(maxLength);
            if (!BrotliEncoder.TryCompress(sourceSpan, destinationSpan, out var written))
                throw new InvalidOperationException("Brotli compression failed.");
            destination.Advance(written);
        }
        finally
        {
            if (scratch is not null)
                bufferWriters.ReturnScratch(scratch);
        }
    }
}
