using System.Buffers;
using System.Runtime.CompilerServices;
using Plank.Schema;

namespace Plank.Writing;

static class DeltaByteArrayEncoding
{
    internal static void WriteValues<T>(Column column, ReadOnlySpan<T> values, ref BufferWriter writer)
        where T : notnull
    {
        if (column.PhysicalType != ParquetPhysicalType.ByteArray)
            throw new NotSupportedException(
                $"Encoding '{EncodingKind.DeltaByteArray}' does not support physical type '{column.PhysicalType}' for column '{column.Name}'.");
        if (typeof(T) != typeof(byte[]))
            throw new InvalidOperationException(
                $"Column '{column.Name}' expects '{ParquetPhysicalType.ByteArray}' values, but got '{typeof(T)}'.");

        var byteArrayValues = Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<byte[]>>(ref values);
        WriteByteArrayValues(column, byteArrayValues, ref writer);
    }

    static void WriteByteArrayValues(Column column, ReadOnlySpan<byte[]> values, ref BufferWriter writer)
    {
        var rentedPrefixLengths = ArrayPool<int>.Shared.Rent(Math.Max(values.Length, 1));
        var rentedSuffixLengths = ArrayPool<int>.Shared.Rent(Math.Max(values.Length, 1));
        var prefixLengths = rentedPrefixLengths.AsSpan(0, values.Length);
        var suffixLengths = rentedSuffixLengths.AsSpan(0, values.Length);

        try
        {
            ReadOnlySpan<byte> previous = [];
            for (var i = 0; i < values.Length; i++)
            {
                var current = values[i] ?? throw new InvalidOperationException(
                    $"Column '{column.Name}' does not support null values.");

                var prefixLength = SharedPrefixLength(previous, current);
                var suffixLength = current.Length - prefixLength;
                prefixLengths[i] = prefixLength;
                suffixLengths[i] = suffixLength;
                previous = current;
            }

            DeltaBinaryPackedEncoding.WriteInt32(prefixLengths, ref writer);
            DeltaBinaryPackedEncoding.WriteInt32(suffixLengths, ref writer);

            for (var i = 0; i < values.Length; i++)
            {
                var current = values[i]!;
                var prefixLength = prefixLengths[i];
                var suffixLength = suffixLengths[i];
                if (suffixLength > 0)
                    writer.Write(current.AsSpan(prefixLength, suffixLength));
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(rentedPrefixLengths);
            ArrayPool<int>.Shared.Return(rentedSuffixLengths);
        }
    }

    static int SharedPrefixLength(ReadOnlySpan<byte> previous, ReadOnlySpan<byte> current)
    {
        var maxLength = Math.Min(previous.Length, current.Length);
        var index = 0;
        while (index < maxLength && previous[index] == current[index])
            index++;
        return index;
    }
}
