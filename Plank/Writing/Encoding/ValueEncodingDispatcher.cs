using Plank.Schema;

namespace Plank.Writing;

static class ValueEncodingDispatcher
{
    internal static void WriteValue<T>(EncodingKind encoding, Column column, T value, ref BufferWriter writer)
        where T : notnull
    {
        switch (encoding)
        {
            case EncodingKind.Plain:
                PlainEncoding.WriteValue(column, value, ref writer);
                return;
            case EncodingKind.Rle:
                RleEncoding.WriteValue(column, value, ref writer);
                return;
            case EncodingKind.BitPacked:
                BitPackedEncoding.WriteValue(column, value, ref writer);
                return;
            case EncodingKind.DeltaBinaryPacked:
                DeltaBinaryPackedEncoding.WriteValue(column, value, ref writer);
                return;
            case EncodingKind.DeltaLengthByteArray:
                DeltaLengthByteArrayEncoding.WriteValue(column, value, ref writer);
                return;
            case EncodingKind.DeltaByteArray:
                DeltaByteArrayEncoding.WriteValue(column, value, ref writer);
                return;
            case EncodingKind.ByteStreamSplit:
                ByteStreamSplitEncoding.WriteValue(column, value, ref writer);
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
