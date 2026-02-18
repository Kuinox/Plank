namespace Plank.Writing;

static class RleDictionaryEncoding
{
    internal static void WriteIndexes(ReadOnlySpan<int> indexes, int dictionaryValueCount, ref BufferWriter writer)
    {
        var maxValue = dictionaryValueCount <= 1 ? 0 : dictionaryValueCount - 1;
        var bitWidth = RleBitPackingHybridEncoding.GetBitWidthFromMaxValue(maxValue);
        RleBitPackingHybridEncoding.WriteWithBitWidthPrefix(indexes, bitWidth, ref writer);
    }
}
