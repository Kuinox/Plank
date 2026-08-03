using System.Buffers.Binary;

namespace Plank.BloomFilters;

static class ParquetBloomFilterHash
{
    internal static ulong Hash(int value)
    {
        Span<byte> encoded = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(encoded, value);
        return XxHash64.Hash(encoded);
    }

    internal static ulong Hash(uint value)
    {
        Span<byte> encoded = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(encoded, value);
        return XxHash64.Hash(encoded);
    }

    internal static ulong Hash(long value)
    {
        Span<byte> encoded = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(encoded, value);
        return XxHash64.Hash(encoded);
    }

    internal static ulong Hash(ulong value)
    {
        Span<byte> encoded = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(encoded, value);
        return XxHash64.Hash(encoded);
    }

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
