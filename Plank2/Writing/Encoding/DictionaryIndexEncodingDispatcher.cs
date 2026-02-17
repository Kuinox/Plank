using Plank.Schema;

namespace Plank2.Writing;

static class DictionaryIndexEncodingDispatcher
{
    internal static void WriteIndex(EncodingKind encoding, int index, ref BufferWriter writer)
    {
        switch (encoding)
        {
            case EncodingKind.PlainDictionary:
                PlainDictionaryEncoding.WriteIndex(index, ref writer);
                return;
            case EncodingKind.RleDictionary:
                RleDictionaryEncoding.WriteIndex(index, ref writer);
                return;
            default:
                throw new InvalidOperationException(
                    $"Encoding '{encoding}' is not a dictionary-index encoding.");
        }
    }
}
