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
        RequireByteArrayColumn(column);
        if (typeof(T) == typeof(byte[]))
        {
            WriteRequiredByteArrayPayloads(column,
                Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<byte[]>>(ref values), bufferWriters, ref writer);
            return;
        }
        if (typeof(T) == typeof(ReadOnlyMemory<byte>))
        {
            WritePayloads<ReadOnlyMemory<byte>, RequiredMemoryRow>(column,
                Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<ReadOnlyMemory<byte>>>(ref values), bufferWriters,
                ref writer);
            return;
        }

        throw new InvalidOperationException(
            $"Column '{column.Name}' expects '{ParquetPhysicalType.ByteArray}' values, but got '{typeof(T)}'.");
    }

    // Required byte[] is a shared-generic reference-type instantiation. A concrete loop avoids the
    // row-access abstraction cost while the optional and memory shapes still share WritePayloads.
    static void WriteRequiredByteArrayPayloads(Column column, ReadOnlySpan<byte[]> values,
        BufferWriterFactory bufferWriters, ref BufferWriter writer)
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
                lengths[i] = value.Length;
                totalPayloadBytes = checked(totalPayloadBytes + value.Length);
            }

            DeltaBinaryPackedEncoding.WriteInt32(lengths, ref writer);
            if (totalPayloadBytes == 0)
                return;

            var destination = writer.GetSpan(totalPayloadBytes);
            var offset = 0;
            for (var i = 0; i < values.Length; i++)
            {
                var value = values[i]!;
                value.CopyTo(destination[offset..]);
                offset += value.Length;
            }

            writer.Advance(offset);
        }
        finally
        {
            bufferWriters.ReturnScratch(rentedLengthsBytes);
        }
    }

    internal static void WriteOptionalValues<TRow, TRowAccess>(Column column, ReadOnlySpan<TRow> rows,
        BufferWriterFactory bufferWriters, ref BufferWriter writer)
        where TRowAccess : IByteArrayRow<TRow>
    {
        if (typeof(TRow) == typeof(byte[]))
        {
            WriteOptionalByteArrayPayloads(column,
                Unsafe.As<ReadOnlySpan<TRow>, ReadOnlySpan<byte[]>>(ref rows), bufferWriters, ref writer);
            return;
        }

        WritePayloads<TRow, TRowAccess>(column, rows, bufferWriters, ref writer);
    }

    static void WriteOptionalByteArrayPayloads(Column column, ReadOnlySpan<byte[]> values,
        BufferWriterFactory bufferWriters, ref BufferWriter writer)
    {
        var presentCount = 0;
        for (var i = 0; i < values.Length; i++)
            if (values[i] is not null)
                presentCount++;
        // Every DELTA_* encoding begins with a mandatory header, so a page with
        // nothing present still has to emit one. Writing nothing produced a data
        // section that neither Plank nor arrow-cpp can decode
        // ("Unexpected end of stream: InitHeader EOF").
        var byteLength = checked(presentCount * sizeof(int));
        var rentedLengthsBytes = bufferWriters.RentScratch(checked((uint)Math.Max(byteLength, sizeof(int))));
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

            var destination = writer.GetSpan(totalPayloadBytes);
            var offset = 0;
            for (var i = 0; i < values.Length; i++)
            {
                var value = values[i];
                if (value is null)
                    continue;
                EncodingPrimitives.CopyPayload(value, destination[offset..]);
                offset += value.Length;
            }

            writer.Advance(offset);
        }
        finally
        {
            bufferWriters.ReturnScratch(rentedLengthsBytes);
        }
    }

    /// <summary>
    /// DELTA_LENGTH_BYTE_ARRAY layout: the delta-binary-packed lengths of every present value,
    /// followed by their bytes back to back. Absent rows contribute nothing, so this serves the
    /// required and optional shapes alike.
    /// </summary>
    static void WritePayloads<TRow, TRowAccess>(Column column, ReadOnlySpan<TRow> rows,
        BufferWriterFactory bufferWriters, ref BufferWriter writer)
        where TRowAccess : IByteArrayRow<TRow>
    {
        var presentCount = ByteArrayRows.CountPresent<TRow, TRowAccess>(rows);
        // Every DELTA_* encoding begins with a mandatory header, so a page with
        // nothing present still has to emit one. Writing nothing produced a data
        // section that neither Plank nor arrow-cpp can decode
        // ("Unexpected end of stream: InitHeader EOF").

        var byteLength = checked(presentCount * sizeof(int));
        var rentedLengthsBytes = bufferWriters.RentScratch(checked((uint)Math.Max(byteLength, sizeof(int))));
        var lengths = MemoryMarshal.Cast<byte, int>(rentedLengthsBytes.Span[..byteLength]);
        var totalPayloadBytes = 0;

        try
        {
            var denseIndex = 0;
            for (var i = 0; i < rows.Length; i++)
            {
                if (!ByteArrayRows.TryGetPayload<TRow, TRowAccess>(column, in rows[i], out var payload))
                    continue;
                lengths[denseIndex++] = payload.Length;
                totalPayloadBytes = checked(totalPayloadBytes + payload.Length);
            }

            DeltaBinaryPackedEncoding.WriteInt32(lengths, ref writer);
            if (totalPayloadBytes == 0)
                return;

            var destination = writer.GetSpan(totalPayloadBytes);
            var offset = 0;
            for (var i = 0; i < rows.Length; i++)
            {
                if (!ByteArrayRows.TryGetPayload<TRow, TRowAccess>(column, in rows[i], out var payload))
                    continue;
                payload.CopyTo(destination[offset..]);
                offset += payload.Length;
            }

            writer.Advance(offset);
        }
        finally
        {
            bufferWriters.ReturnScratch(rentedLengthsBytes);
        }
    }

    static void RequireByteArrayColumn(Column column)
    {
        if (column.PhysicalType != ParquetPhysicalType.ByteArray)
            throw new NotSupportedException(
                $"Encoding '{EncodingKind.DeltaLengthByteArray}' does not support physical type '{column.PhysicalType}' for column '{column.Name}'.");
    }
}
