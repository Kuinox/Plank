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
            case EncodingKind.PlainDictionary:
            case EncodingKind.RleDictionary:
                throw new InvalidOperationException(
                    $"Value encoding '{encoding}' is dictionary-only and cannot be used for non-dictionary values.");
            default:
                throw new NotSupportedException($"Encoding '{encoding}' is not supported.");
        }
    }

    internal static void WriteOptionalByteArrayValues(EncodingKind encoding, Column column,
        ReadOnlySpan<byte[]> values, BufferWriterFactory bufferWriters, ref BufferWriter writer)
    {
        switch (encoding)
        {
            case EncodingKind.Plain:
                PlainEncoding.WriteOptionalByteArrayValues(column, values, ref writer);
                return;
            case EncodingKind.DeltaLengthByteArray:
                DeltaLengthByteArrayEncoding.WriteOptionalByteArrayValues(column, values, bufferWriters, ref writer);
                return;
            case EncodingKind.DeltaByteArray:
                DeltaByteArrayEncoding.WriteOptionalByteArrayValues(column, values, bufferWriters, ref writer);
                return;
            default:
                throw new NotSupportedException(
                    $"Encoding '{encoding}' is not supported for optional byte-array values.");
        }
    }

    internal static void WriteOptionalMemoryValues(EncodingKind encoding, Column column,
        ReadOnlySpan<ReadOnlyMemory<byte>?> values, BufferWriterFactory bufferWriters, ref BufferWriter writer)
    {
        switch (encoding)
        {
            case EncodingKind.Plain:
                PlainEncoding.WriteOptionalMemoryValues(column, values, ref writer);
                return;
            case EncodingKind.DeltaLengthByteArray:
                DeltaLengthByteArrayEncoding.WriteOptionalMemoryValues(column, values, bufferWriters, ref writer);
                return;
            case EncodingKind.DeltaByteArray:
                DeltaByteArrayEncoding.WriteOptionalMemoryValues(column, values, bufferWriters, ref writer);
                return;
            default:
                throw new NotSupportedException(
                    $"Encoding '{encoding}' is not supported for optional byte-array values.");
        }
    }
}
