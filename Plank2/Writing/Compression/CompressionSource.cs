namespace Plank2.Writing;

static class CompressionSource
{
    internal static ReadOnlySpan<byte> AsContiguous(BufferWriterFactory bufferWriters, ref BufferWriter source,
        out byte[]? scratch)
    {
        if (source.TryGetSingleWrittenSpan(out var sourceSpan))
        {
            scratch = null;
            return sourceSpan;
        }

        scratch = bufferWriters.RentScratch(source.WrittenLength);
        var scratchSpan = scratch.AsSpan(0, source.WrittenLength);
        source.CopyTo(scratchSpan);
        return scratchSpan;
    }
}
