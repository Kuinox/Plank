using System.Runtime.CompilerServices;
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
        if (typeof(T) != typeof(byte[]))
            throw new InvalidOperationException(
                $"Column '{column.Name}' expects '{ParquetPhysicalType.ByteArray}' values, but got '{typeof(T)}'.");

        var byteArrayValues = Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<byte[]>>(ref values);
        WriteByteArrayValues(column, byteArrayValues, bufferWriters, ref writer);
    }

    static void WriteByteArrayValues(Column column, ReadOnlySpan<byte[]> values, BufferWriterFactory bufferWriters,
        ref BufferWriter writer)
    {
        var byteLength = checked(values.Length * sizeof(int));
        var rentedLengthsBytes = bufferWriters.RentScratch(checked((uint)Math.Max(byteLength, sizeof(int))));
        var lengths = MemoryMarshal.Cast<byte, int>(rentedLengthsBytes.AsSpan(0, byteLength));
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
}
