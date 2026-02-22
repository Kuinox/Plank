namespace Plank.Writing.Encoding;

static class RleDictionaryEncoding
{
    internal static void WriteIndexes(ReadOnlySpan<int> indexes, int bitWidth, ref BufferWriter writer)
        => RleBitPackingHybridEncoding.WriteWithBitWidthPrefixUnchecked(indexes, bitWidth, ref writer);
}
