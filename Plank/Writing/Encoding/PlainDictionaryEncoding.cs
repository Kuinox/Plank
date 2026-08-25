namespace Plank.Writing.Encoding;

static class PlainDictionaryEncoding
{
    internal static void WriteIndexes(ReadOnlySpan<int> indexes, int bitWidth, ref BufferWriter writer)
        => RleBitPackingHybridEncoding.WriteWithBitWidthPrefixUnchecked(indexes, bitWidth, ref writer);
}
