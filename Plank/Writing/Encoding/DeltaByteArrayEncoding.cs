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
            var byteArrayValues = SpanReinterpretation.Cast<T, byte[]>(values);
            WriteByteArrayValues(column, byteArrayValues, bufferWriters, ref writer);
            return;
        }
        if (typeof(T) == typeof(ReadOnlyMemory<byte>))
        {
            var memoryValues = SpanReinterpretation.Cast<T, ReadOnlyMemory<byte>>(values);
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
        var prefixLengths = MemoryMarshal.Cast<byte, int>(rentedPrefixLengthsBytes.Span[..byteLength]);
        var suffixLengths = MemoryMarshal.Cast<byte, int>(rentedSuffixLengthsBytes.Span[..byteLength]);
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
        var prefixLengths = MemoryMarshal.Cast<byte, int>(rentedPrefixLengthsBytes.Span[..byteLength]);
        var suffixLengths = MemoryMarshal.Cast<byte, int>(rentedSuffixLengthsBytes.Span[..byteLength]);
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

    internal static void WriteOptionalByteArrayValues(Column column, ReadOnlySpan<byte[]> values,
        BufferWriterFactory bufferWriters, ref BufferWriter writer)
    {
        var presentCount = 0;
        for (var i = 0; i < values.Length; i++)
            if (values[i] is not null)
                presentCount++;
        if (presentCount == 0)
            return;

        var byteLength = checked(presentCount * sizeof(int));
        var rentedPrefixLengthsBytes = bufferWriters.RentScratch(checked((uint)byteLength));
        var rentedSuffixLengthsBytes = bufferWriters.RentScratch(checked((uint)byteLength));
        var prefixLengths = MemoryMarshal.Cast<byte, int>(rentedPrefixLengthsBytes.Span[..byteLength]);
        var suffixLengths = MemoryMarshal.Cast<byte, int>(rentedSuffixLengthsBytes.Span[..byteLength]);
        var totalSuffixBytes = 0;

        try
        {
            ReadOnlySpan<byte> previous = [];
            var denseIndex = 0;
            for (var i = 0; i < values.Length; i++)
            {
                var current = values[i];
                if (current is null)
                    continue;

                var prefixLength = SharedPrefixLength(previous, current);
                var suffixLength = current.Length - prefixLength;
                prefixLengths[denseIndex] = prefixLength;
                suffixLengths[denseIndex] = suffixLength;
                denseIndex++;
                totalSuffixBytes = checked(totalSuffixBytes + suffixLength);
                previous = current;
            }

            DeltaBinaryPackedEncoding.WriteInt32(prefixLengths, ref writer);
            DeltaBinaryPackedEncoding.WriteInt32(suffixLengths, ref writer);
            if (totalSuffixBytes == 0)
                return;

            var suffixDestination = writer.GetSpan(totalSuffixBytes);
            var suffixOffset = 0;
            denseIndex = 0;
            for (var i = 0; i < values.Length; i++)
            {
                var current = values[i];
                if (current is null)
                    continue;

                var prefixLength = prefixLengths[denseIndex];
                var suffixLength = suffixLengths[denseIndex++];
                current.AsSpan(prefixLength, suffixLength).CopyTo(suffixDestination[suffixOffset..]);
                suffixOffset += suffixLength;
            }

            writer.Advance(suffixOffset);
        }
        finally
        {
            bufferWriters.ReturnScratch(rentedPrefixLengthsBytes);
            bufferWriters.ReturnScratch(rentedSuffixLengthsBytes);
        }
    }

    internal static void WriteOptionalMemoryValues(Column column, ReadOnlySpan<ReadOnlyMemory<byte>?> values,
        BufferWriterFactory bufferWriters, ref BufferWriter writer)
    {
        var presentCount = 0;
        for (var i = 0; i < values.Length; i++)
            if (values[i].HasValue)
                presentCount++;
        if (presentCount == 0)
            return;

        var byteLength = checked(presentCount * sizeof(int));
        var rentedPrefixLengthsBytes = bufferWriters.RentScratch(checked((uint)byteLength));
        var rentedSuffixLengthsBytes = bufferWriters.RentScratch(checked((uint)byteLength));
        var prefixLengths = MemoryMarshal.Cast<byte, int>(rentedPrefixLengthsBytes.Span[..byteLength]);
        var suffixLengths = MemoryMarshal.Cast<byte, int>(rentedSuffixLengthsBytes.Span[..byteLength]);
        var totalSuffixBytes = 0;

        try
        {
            ReadOnlySpan<byte> previous = [];
            var denseIndex = 0;
            for (var i = 0; i < values.Length; i++)
            {
                var nullableValue = values[i];
                if (!nullableValue.HasValue)
                    continue;

                var current = nullableValue.Value.Span;
                var prefixLength = SharedPrefixLength(previous, current);
                var suffixLength = current.Length - prefixLength;
                prefixLengths[denseIndex] = prefixLength;
                suffixLengths[denseIndex] = suffixLength;
                denseIndex++;
                totalSuffixBytes = checked(totalSuffixBytes + suffixLength);
                previous = current;
            }

            DeltaBinaryPackedEncoding.WriteInt32(prefixLengths, ref writer);
            DeltaBinaryPackedEncoding.WriteInt32(suffixLengths, ref writer);
            if (totalSuffixBytes == 0)
                return;

            var suffixDestination = writer.GetSpan(totalSuffixBytes);
            var suffixOffset = 0;
            denseIndex = 0;
            for (var i = 0; i < values.Length; i++)
            {
                var nullableValue = values[i];
                if (!nullableValue.HasValue)
                    continue;

                var current = nullableValue.Value.Span;
                var prefixLength = prefixLengths[denseIndex];
                var suffixLength = suffixLengths[denseIndex++];
                current.Slice(prefixLength, suffixLength).CopyTo(suffixDestination[suffixOffset..]);
                suffixOffset += suffixLength;
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
