using System.Runtime.InteropServices;
using Plank.Schema;

namespace Plank.Writing.Encoding;

static class DeltaLengthByteArrayEncoding
{
    internal static void WriteValues<T>(Column column, ReadOnlySpan<T> values, BufferWriterFactory bufferWriters,
        ref BufferWriter writer)
        where T : notnull
    {
        if (column.PhysicalType != ParquetPhysicalType.ByteArray)
            throw new NotSupportedException(
                $"Encoding '{EncodingKind.DeltaLengthByteArray}' does not support physical type '{column.PhysicalType}' for column '{column.Name}'.");
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
        var rentedLengthsBytes = bufferWriters.RentScratch(checked((uint)Math.Max(byteLength, sizeof(int))));
        var lengths = MemoryMarshal.Cast<byte, int>(rentedLengthsBytes.Span[..byteLength]);
        var totalPayloadBytes = 0;

        try
        {
            for (var i = 0; i < values.Length; i++)
            {
                var value = values[i] ?? throw new InvalidOperationException(
                    $"Column '{column.Name}' does not support null values.");
                var length = value.Length;
                lengths[i] = length;
                totalPayloadBytes = checked(totalPayloadBytes + length);
            }

            DeltaBinaryPackedEncoding.WriteInt32(lengths, ref writer);
            if (totalPayloadBytes == 0)
                return;

            var payload = writer.GetSpan(totalPayloadBytes);
            var payloadOffset = 0;
            for (var i = 0; i < values.Length; i++)
            {
                var value = values[i]!;
                value.CopyTo(payload[payloadOffset..]);
                payloadOffset += value.Length;
            }

            writer.Advance(payloadOffset);
        }
        finally
        {
            bufferWriters.ReturnScratch(rentedLengthsBytes);
        }
    }

    static void WriteMemoryValues(Column column, ReadOnlySpan<ReadOnlyMemory<byte>> values, BufferWriterFactory bufferWriters,
        ref BufferWriter writer)
    {
        var byteLength = checked(values.Length * sizeof(int));
        var rentedLengthsBytes = bufferWriters.RentScratch(checked((uint)Math.Max(byteLength, sizeof(int))));
        var lengths = MemoryMarshal.Cast<byte, int>(rentedLengthsBytes.Span[..byteLength]);
        var totalPayloadBytes = 0;

        try
        {
            for (var i = 0; i < values.Length; i++)
            {
                var length = values[i].Length;
                lengths[i] = length;
                totalPayloadBytes = checked(totalPayloadBytes + length);
            }

            DeltaBinaryPackedEncoding.WriteInt32(lengths, ref writer);
            if (totalPayloadBytes == 0)
                return;

            var payload = writer.GetSpan(totalPayloadBytes);
            var payloadOffset = 0;
            for (var i = 0; i < values.Length; i++)
            {
                var value = values[i];
                value.Span.CopyTo(payload[payloadOffset..]);
                payloadOffset += value.Length;
            }

            writer.Advance(payloadOffset);
        }
        finally
        {
            bufferWriters.ReturnScratch(rentedLengthsBytes);
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
        var rentedLengthsBytes = bufferWriters.RentScratch(checked((uint)byteLength));
        var lengths = MemoryMarshal.Cast<byte, int>(rentedLengthsBytes.Span[..byteLength]);
        var totalPayloadBytes = 0;

        try
        {
            var denseIndex = 0;
            for (var i = 0; i < values.Length; i++)
            {
                var value = values[i];
                if (value is null)
                    continue;
                lengths[denseIndex++] = value.Length;
                totalPayloadBytes = checked(totalPayloadBytes + value.Length);
            }

            DeltaBinaryPackedEncoding.WriteInt32(lengths, ref writer);
            if (totalPayloadBytes == 0)
                return;

            var payload = writer.GetSpan(totalPayloadBytes);
            var payloadOffset = 0;
            for (var i = 0; i < values.Length; i++)
            {
                var value = values[i];
                if (value is null)
                    continue;
                value.CopyTo(payload[payloadOffset..]);
                payloadOffset += value.Length;
            }

            writer.Advance(payloadOffset);
        }
        finally
        {
            bufferWriters.ReturnScratch(rentedLengthsBytes);
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
        var rentedLengthsBytes = bufferWriters.RentScratch(checked((uint)byteLength));
        var lengths = MemoryMarshal.Cast<byte, int>(rentedLengthsBytes.Span[..byteLength]);
        var totalPayloadBytes = 0;

        try
        {
            var denseIndex = 0;
            for (var i = 0; i < values.Length; i++)
            {
                var nullableValue = values[i];
                if (!nullableValue.HasValue)
                    continue;
                var value = nullableValue.Value;
                lengths[denseIndex++] = value.Length;
                totalPayloadBytes = checked(totalPayloadBytes + value.Length);
            }

            DeltaBinaryPackedEncoding.WriteInt32(lengths, ref writer);
            if (totalPayloadBytes == 0)
                return;

            var payload = writer.GetSpan(totalPayloadBytes);
            var payloadOffset = 0;
            for (var i = 0; i < values.Length; i++)
            {
                var nullableValue = values[i];
                if (!nullableValue.HasValue)
                    continue;
                var value = nullableValue.Value;
                value.Span.CopyTo(payload[payloadOffset..]);
                payloadOffset += value.Length;
            }

            writer.Advance(payloadOffset);
        }
        finally
        {
            bufferWriters.ReturnScratch(rentedLengthsBytes);
        }
    }
}
