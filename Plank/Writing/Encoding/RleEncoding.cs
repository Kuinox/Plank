using System.Buffers.Binary;
using Plank.Schema;

namespace Plank.Writing.Encoding;

static class RleEncoding
{
    internal static void WriteValues<T>(Column column, ReadOnlySpan<T> values, ref BufferWriter writer)
        where T : notnull
    {
        if (column.PhysicalType != ParquetPhysicalType.Boolean)
            throw new NotSupportedException(
                $"Encoding '{EncodingKind.Rle}' supports only Boolean columns. Column '{column.Name}' is '{column.PhysicalType}'.");
        if (typeof(T) != typeof(bool))
            throw new InvalidOperationException(
                $"Column '{column.Name}' expects '{ParquetPhysicalType.Boolean}' values, but got '{typeof(T)}'.");

        var booleanValues = SpanReinterpretation.Cast<T, bool>(values);
        var lengthPrefix = writer.GetSpan(sizeof(int));
        writer.Advance(sizeof(int));
        var encodedStart = writer.WrittenLength;
        RleBitPackingHybridEncoding.WriteBooleans(booleanValues, ref writer);
        BinaryPrimitives.WriteInt32LittleEndian(lengthPrefix, writer.WrittenLength - encodedStart);
    }
}
