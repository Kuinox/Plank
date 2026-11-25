namespace Plank.BloomFilters;

static class ParquetBloomFilterHash
{
    internal static ulong Hash(int value) => XxHash64.Hash(unchecked((uint)value));

    internal static ulong Hash(uint value) => XxHash64.Hash(value);

    internal static ulong Hash(long value) => XxHash64.Hash(unchecked((ulong)value));

    internal static ulong Hash(ulong value) => XxHash64.Hash(value);

    internal static ulong Hash(float value)
        => Hash(unchecked((uint)BitConverter.SingleToInt32Bits(value)));

    internal static ulong Hash(double value)
        => Hash(unchecked((ulong)BitConverter.DoubleToInt64Bits(value)));

    internal static ulong Hash(Guid value)
    {
        Span<byte> encoded = stackalloc byte[16];
        value.TryWriteBytes(encoded, bigEndian: true, out _);
        return XxHash64.Hash(encoded);
    }

    internal static ulong Hash(ReadOnlySpan<byte> value)
        => XxHash64.Hash(value);
}
