using System.Buffers;
using System.Runtime.CompilerServices;
using Plank.Schema;

namespace Plank.Writing;

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

        var booleanValues = Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<bool>>(ref values);
        var rentedValues = ArrayPool<int>.Shared.Rent(Math.Max(booleanValues.Length, 1));
        var encodedValues = rentedValues.AsSpan(0, booleanValues.Length);

        try
        {
            for (var i = 0; i < booleanValues.Length; i++)
                encodedValues[i] = booleanValues[i] ? 1 : 0;

            RleBitPackingHybridEncoding.Write(encodedValues, 1, ref writer);
        }
        finally
        {
            ArrayPool<int>.Shared.Return(rentedValues);
        }
    }
}
