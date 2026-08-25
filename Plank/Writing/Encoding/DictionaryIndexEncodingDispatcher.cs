using Plank.Schema;

namespace Plank.Writing.Encoding;

static class DictionaryIndexEncodingDispatcher
{
    internal static void WriteIndexes(EncodingKind encoding, ReadOnlySpan<int> indexes, int bitWidth,
        ref BufferWriter writer)
    {
        switch (encoding)
        {
            case EncodingKind.PlainDictionary:
                PlainDictionaryEncoding.WriteIndexes(indexes, bitWidth, ref writer);
                return;
            case EncodingKind.RleDictionary:
                RleDictionaryEncoding.WriteIndexes(indexes, bitWidth, ref writer);
                return;
            default:
                throw new InvalidOperationException(
                    $"Encoding '{encoding}' is not a dictionary-index encoding.");
        }
    }
}
