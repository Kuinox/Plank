using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Plank.Schema;

namespace Plank.Writing.Encoding;

static class DeltaByteArrayEncoding
{
    internal static void WriteValues<T>(Column column, ReadOnlySpan<T> values, BufferWriterFactory bufferWriters,
        ref BufferWriter writer)
        where T : notnull
    {
        if (column.PhysicalType != ParquetPhysicalType.ByteArray)
            throw new NotSupportedException(
                $"Encoding '{EncodingKind.DeltaByteArray}' does not support physical type '{column.PhysicalType}' for column '{column.Name}'.");
        if (typeof(T) == typeof(byte[]))
        {
            var byteArrayValues = Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<byte[]>>(ref values);
            WriteByteArrayValues(column, byteArrayValues, bufferWriters, ref writer);
            return;
        }
        if (typeof(T) == typeof(ReadOnlyMemory<byte>))
        {
            var memoryValues = Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<ReadOnlyMemory<byte>>>(ref values);
            WriteMemoryValues(column, memoryValues, bufferWriters, ref writer);
            return;
        }

        throw new InvalidOperationException(
            $"Column '{column.Name}' expects '{ParquetPhysicalType.ByteArray}' values, but got '{typeof(T)}'.");
    }

    static void WriteByteArrayValues(Column column, ReadOnlySpan<byte[]> values, BufferWriterFactory bufferWriters,
        ref BufferWriter writer)
    {
        var byteLength = checked(values.Length * sizeof(int));
        var rentedPrefixLengthsBytes = bufferWriters.RentScratch(checked((uint)Math.Max(byteLength, sizeof(int))));
        var rentedSuffixLengthsBytes = bufferWriters.RentScratch(checked((uint)Math.Max(byteLength, sizeof(int))));
        var prefixLengths = MemoryMarshal.Cast<byte, int>(rentedPrefixLengthsBytes.AsSpan(0, byteLength));
        var suffixLengths = MemoryMarshal.Cast<byte, int>(rentedSuffixLengthsBytes.AsSpan(0, byteLength));
        var totalSuffixBytes = 0;

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
                totalSuffixBytes = checked(totalSuffixBytes + suffixLength);
                previous = current;
            }

            DeltaBinaryPackedEncoding.WriteInt32(prefixLengths, ref writer);
            DeltaBinaryPackedEncoding.WriteInt32(suffixLengths, ref writer);

            if (totalSuffixBytes == 0)
                return;

            var suffixDestination = writer.GetSpan(totalSuffixBytes);
            var suffixOffset = 0;
            for (var i = 0; i < values.Length; i++)
            {
                var current = values[i]!;
                var prefixLength = prefixLengths[i];
                var suffixLength = suffixLengths[i];
                if (suffixLength > 0)
                {
                    current.AsSpan(prefixLength, suffixLength).CopyTo(suffixDestination[suffixOffset..]);
                    suffixOffset += suffixLength;
                }
            }

            writer.Advance(suffixOffset);
        }
        finally
        {
            bufferWriters.ReturnScratch(rentedPrefixLengthsBytes);
            bufferWriters.ReturnScratch(rentedSuffixLengthsBytes);
        }
    }

    static void WriteMemoryValues(Column column, ReadOnlySpan<ReadOnlyMemory<byte>> values, BufferWriterFactory bufferWriters,
        ref BufferWriter writer)
    {
        var byteLength = checked(values.Length * sizeof(int));
        var rentedPrefixLengthsBytes = bufferWriters.RentScratch(checked((uint)Math.Max(byteLength, sizeof(int))));
        var rentedSuffixLengthsBytes = bufferWriters.RentScratch(checked((uint)Math.Max(byteLength, sizeof(int))));
        var prefixLengths = MemoryMarshal.Cast<byte, int>(rentedPrefixLengthsBytes.AsSpan(0, byteLength));
        var suffixLengths = MemoryMarshal.Cast<byte, int>(rentedSuffixLengthsBytes.AsSpan(0, byteLength));
        var totalSuffixBytes = 0;

        try
        {
            ReadOnlySpan<byte> previous = [];
            for (var i = 0; i < values.Length; i++)
            {
                var current = values[i].Span;
                var prefixLength = SharedPrefixLength(previous, current);
                var suffixLength = current.Length - prefixLength;
                prefixLengths[i] = prefixLength;
                suffixLengths[i] = suffixLength;
                totalSuffixBytes = checked(totalSuffixBytes + suffixLength);
                previous = current;
            }

            DeltaBinaryPackedEncoding.WriteInt32(prefixLengths, ref writer);
            DeltaBinaryPackedEncoding.WriteInt32(suffixLengths, ref writer);

            if (totalSuffixBytes == 0)
                return;

            var suffixDestination = writer.GetSpan(totalSuffixBytes);
            var suffixOffset = 0;
            for (var i = 0; i < values.Length; i++)
            {
                var current = values[i].Span;
                var prefixLength = prefixLengths[i];
                var suffixLength = suffixLengths[i];
                if (suffixLength > 0)
                {
                    current.Slice(prefixLength, suffixLength).CopyTo(suffixDestination[suffixOffset..]);
                    suffixOffset += suffixLength;
                }
            }

            writer.Advance(suffixOffset);
        }
        finally
        {
            bufferWriters.ReturnScratch(rentedPrefixLengthsBytes);
            bufferWriters.ReturnScratch(rentedSuffixLengthsBytes);
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
