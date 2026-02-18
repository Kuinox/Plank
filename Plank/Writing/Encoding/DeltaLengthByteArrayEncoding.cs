using System.Buffers;
using System.Runtime.CompilerServices;
using Plank.Schema;

namespace Plank.Writing;

static class DeltaLengthByteArrayEncoding
{
    internal static void WriteValues<T>(Column column, ReadOnlySpan<T> values, ref BufferWriter writer)
        where T : notnull
    {
        if (column.PhysicalType != ParquetPhysicalType.ByteArray)
            throw new NotSupportedException(
                $"Encoding '{EncodingKind.DeltaLengthByteArray}' does not support physical type '{column.PhysicalType}' for column '{column.Name}'.");
        if (typeof(T) != typeof(byte[]))
            throw new InvalidOperationException(
                $"Column '{column.Name}' expects '{ParquetPhysicalType.ByteArray}' values, but got '{typeof(T)}'.");

        var byteArrayValues = Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<byte[]>>(ref values);
        WriteByteArrayValues(column, byteArrayValues, ref writer);
    }

    static void WriteByteArrayValues(Column column, ReadOnlySpan<byte[]> values, ref BufferWriter writer)
    {
        var rentedLengths = ArrayPool<int>.Shared.Rent(Math.Max(values.Length, 1));
        var lengths = rentedLengths.AsSpan(0, values.Length);

        try
        {
            for (var i = 0; i < values.Length; i++)
            {
                var value = values[i] ?? throw new InvalidOperationException(
                    $"Column '{column.Name}' does not support null values.");
                lengths[i] = value.Length;
            }

            DeltaBinaryPackedEncoding.WriteInt32(lengths, ref writer);
            for (var i = 0; i < values.Length; i++)
                writer.Write(values[i]);
        }
        finally
        {
            ArrayPool<int>.Shared.Return(rentedLengths);
        }
    }
}
