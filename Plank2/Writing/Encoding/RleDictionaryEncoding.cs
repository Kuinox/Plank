namespace Plank2.Writing;

static class RleDictionaryEncoding
{
    internal static void WriteIndex(int index, ref BufferWriter writer)
        => PlainDictionaryEncoding.WriteIndex(index, ref writer);
}
