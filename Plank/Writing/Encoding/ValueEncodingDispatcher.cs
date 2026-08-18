using Plank.Schema;

namespace Plank.Writing.Encoding;

static class ValueEncodingDispatcher
{
    internal static void WriteValues<T>(EncodingKind encoding, Column column, ReadOnlySpan<T> values,
        BufferWriterFactory bufferWriters, ref BufferWriter writer)
        where T : notnull
    {
        switch (encoding)
        {
            case EncodingKind.Plain:
                PlainEncoding.WriteValues(column, values, ref writer);
                return;
            case EncodingKind.Rle:
                RleEncoding.WriteValues(column, values, ref writer);
                return;
            case EncodingKind.BitPacked:
                BitPackedEncoding.WriteValues(column, values, ref writer);
                return;
            case EncodingKind.DeltaBinaryPacked:
                DeltaBinaryPackedEncoding.WriteValues(column, values, ref writer);
                return;
            case EncodingKind.DeltaLengthByteArray:
                DeltaLengthByteArrayEncoding.WriteValues(column, values, bufferWriters, ref writer);
                return;
            case EncodingKind.DeltaByteArray:
                DeltaByteArrayEncoding.WriteValues(column, values, bufferWriters, ref writer);
                return;
            case EncodingKind.ByteStreamSplit:
                ByteStreamSplitEncoding.WriteValues(column, values, ref writer);
                return;
            case EncodingKind.Alp:
                AlpEncoding.WriteValues(column, values, bufferWriters, ref writer);
                return;
            case EncodingKind.PlainDictionary:
            case EncodingKind.RleDictionary:
                throw new InvalidOperationException(
                    $"Value encoding '{encoding}' is dictionary-only and cannot be used for non-dictionary values.");
            default:
                throw new NotSupportedException($"Encoding '{encoding}' is not supported.");
        }
    }

    /// <summary>
    /// Writes the present values of one optional byte-array page straight from the rows. The row
    /// shape - <c>byte[]</c> or <see cref="ReadOnlyMemory{T}"/> - is carried by
    /// <typeparamref name="TRowAccess"/> rather than by a separate overload per shape.
    /// </summary>
    internal static void WriteOptionalValues<TRow, TRowAccess>(EncodingKind encoding, Column column,
        ReadOnlySpan<TRow> rows, BufferWriterFactory bufferWriters, ref BufferWriter writer)
        where TRowAccess : IByteArrayRow<TRow>
    {
        switch (encoding)
        {
            case EncodingKind.Plain:
                PlainEncoding.WriteOptionalValues<TRow, TRowAccess>(column, rows, ref writer);
                return;
            case EncodingKind.DeltaLengthByteArray:
                DeltaLengthByteArrayEncoding.WriteOptionalValues<TRow, TRowAccess>(column, rows, bufferWriters,
                    ref writer);
                return;
            case EncodingKind.DeltaByteArray:
                DeltaByteArrayEncoding.WriteOptionalValues<TRow, TRowAccess>(column, rows, bufferWriters, ref writer);
                return;
            default:
                throw new NotSupportedException(
                    $"Encoding '{encoding}' is not supported for optional byte-array values.");
        }
    }
}
