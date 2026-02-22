using Plank.Schema;

namespace Plank.Writing.Encoding;

static class BitPackedEncoding
{
    internal static void WriteValues<T>(Column column, ReadOnlySpan<T> values, ref BufferWriter writer)
        where T : notnull
        => throw new NotSupportedException(
            $"Encoding '{EncodingKind.BitPacked}' is deprecated and not supported for data values in this writer (column '{column.Name}').");
}
