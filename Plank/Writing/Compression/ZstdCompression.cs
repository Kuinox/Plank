using ZstdSharp;

namespace Plank.Writing;

static class ZstdCompression
{
    internal static void Compress(BufferWriterFactory bufferWriters, ref BufferWriter source, ref BufferWriter destination)
    {
        var sourceSpan = CompressionSource.AsContiguous(bufferWriters, ref source, out var scratch);
        try
        {
            using var compressor = new Compressor(1);
            compressor.SetPledgedSrcSize((ulong)sourceSpan.Length);
            var maxLength = Compressor.GetCompressBound(sourceSpan.Length);
            var destinationSpan = destination.GetSpan(maxLength);
            var written = compressor.Wrap(sourceSpan, destinationSpan);
            destination.Advance(written);
        }
        finally
        {
            if (scratch is not null)
                bufferWriters.ReturnScratch(scratch);
        }
    }
}
