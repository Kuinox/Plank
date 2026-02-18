using Plank.Schema;

namespace Plank.Writing;

static class DictionaryIndexEncodingDispatcher
{
    internal static void WriteIndexes(EncodingKind encoding, ReadOnlySpan<int> indexes, int dictionaryValueCount,
        ref BufferWriter writer)
    {
        switch (encoding)
        {
            case EncodingKind.PlainDictionary:
                PlainDictionaryEncoding.WriteIndexes(indexes, dictionaryValueCount, ref writer);
                return;
            case EncodingKind.RleDictionary:
                RleDictionaryEncoding.WriteIndexes(indexes, dictionaryValueCount, ref writer);
                return;
            default:
                throw new InvalidOperationException(
                    $"Encoding '{encoding}' is not a dictionary-index encoding.");
        }
    }
}
