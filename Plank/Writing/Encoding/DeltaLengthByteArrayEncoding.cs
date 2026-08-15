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
            WritePayloads<byte[], RequiredByteArrayRow>(column,
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

    internal static void WriteOptionalValues<TRow, TRowAccess>(Column column, ReadOnlySpan<TRow> rows,
        BufferWriterFactory bufferWriters, ref BufferWriter writer)
        where TRowAccess : IByteArrayRow<TRow>
        => WritePayloads<TRow, TRowAccess>(column, rows, bufferWriters, ref writer);

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
        // An optional page with no present value writes nothing at all, while a required page always
        // emits the length header even when it is empty.
        if (!TRowAccess.ValueRequired && presentCount == 0)
            return;

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
