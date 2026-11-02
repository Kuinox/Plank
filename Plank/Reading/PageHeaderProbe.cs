namespace Plank.Reading;

using Plank.Reading.Physical;

/// <summary>Reads an unframed page header without crossing its enclosing boundary.</summary>
static class PageHeaderProbe
{
    const int ExactProbeCount = 128;
    internal const int CursorInitialLength = 64 * 1024;

    // Metadata starts with one byte to avoid payload I/O on ordinary headers. The cursor
    // starts with a larger window because it will read the payload anyway. After a fixed
    // number of exact-shortfall probes both grow geometrically, bounding reparse work.
    internal static PageHeader Read(ParquetFileReader reader, ulong offset, int maximumLength,
        uint maxUncompressedPageSize, int initialLength, bool allowBorrow, ref ParquetBuffer buffer)
    {
        var length = 0;
        var wanted = Math.Min(maximumLength, initialLength);
        var missingBytes = 1;
        var probes = 0;
        while (true)
        {
            wanted = Math.Max(wanted, PageHeaderReader.GetRequiredBufferLength(length, missingBytes, maximumLength));
            if (probes >= ExactProbeCount)
                wanted = (int)Math.Min(maximumLength, Math.Max((long)wanted, (long)length * 2));

            ReadOnlySpan<byte> bytes;
            if (allowBorrow && reader.TryBorrowSource(offset, wanted, out var borrowed))
                bytes = borrowed.Span;
            else
            {
                if (wanted > buffer.Length)
                {
                    var capacity = (uint)Math.Min(maximumLength, Math.Max((long)buffer.Length * 2, wanted));
                    var grown = reader.BufferPool.Rent(capacity);
                    buffer.Span[..length].CopyTo(grown.Span);
                    buffer.Dispose();
                    buffer = grown;
                }
                reader.Source.ReadExactly(offset + (ulong)length, buffer.Span[length..wanted]);
                bytes = buffer.Span[..wanted];
            }
            length = wanted;
            probes++;
            if (PageHeaderReader.TryRead(bytes, maxUncompressedPageSize, out var header, out missingBytes))
                return header;
        }
    }
}
