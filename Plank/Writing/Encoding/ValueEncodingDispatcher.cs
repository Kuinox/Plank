using Plank.Schema;

namespace Plank.Writing;

static class ValueEncodingDispatcher
{
    internal static void WriteValues<T>(EncodingKind encoding, Column column, ReadOnlySpan<T> values,
        ref BufferWriter writer)
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
                DeltaLengthByteArrayEncoding.WriteValues(column, values, ref writer);
                return;
            case EncodingKind.DeltaByteArray:
                DeltaByteArrayEncoding.WriteValues(column, values, ref writer);
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
}
