using System.Buffers.Binary;

namespace Plank.BloomFilters;

static class SplitBlockBloomFilter
{
    const int BytesPerBlock = 32;

    static readonly uint[] _salt =
    [
        0x47b6137bU, 0x44974d91U, 0x8824ad5bU, 0xa2b7289dU,
        0x705495c7U, 0x2df1424bU, 0x9efc4947U, 0x5c6bfb31U
    ];

    static ReadOnlySpan<uint> Salt
        => _salt;

    internal static void InsertHash(Span<byte> bitset, ulong hash)
    {
        var block = GetBlock(bitset, hash);
        var key = unchecked((uint)hash);
        var salt = Salt;
        for (var i = 0; i < salt.Length; i++)
        {
            var wordOffset = i * sizeof(uint);
            var word = BinaryPrimitives.ReadUInt32LittleEndian(block[wordOffset..]);
            var bit = unchecked(key * salt[i]) >> 27;
            BinaryPrimitives.WriteUInt32LittleEndian(block[wordOffset..], word | (1U << (int)bit));
        }
    }

    internal static bool MightContainHash(ReadOnlySpan<byte> bitset, ulong hash)
    {
        var block = GetBlock(bitset, hash);
        var key = unchecked((uint)hash);
        var salt = Salt;
        for (var i = 0; i < salt.Length; i++)
        {
            var word = BinaryPrimitives.ReadUInt32LittleEndian(block[(i * sizeof(uint))..]);
            var bit = unchecked(key * salt[i]) >> 27;
            if ((word & (1U << (int)bit)) == 0)
                return false;
        }

        return true;
    }

    static Span<byte> GetBlock(Span<byte> bitset, ulong hash)
    {
        ValidateBitset(bitset);
        var blockCount = checked((uint)(bitset.Length / BytesPerBlock));
        var blockIndex = (uint)(((hash >> 32) * blockCount) >> 32);
        return bitset.Slice(checked((int)blockIndex * BytesPerBlock), BytesPerBlock);
    }

    static ReadOnlySpan<byte> GetBlock(ReadOnlySpan<byte> bitset, ulong hash)
    {
        ValidateBitset(bitset);
        var blockCount = checked((uint)(bitset.Length / BytesPerBlock));
        var blockIndex = (uint)(((hash >> 32) * blockCount) >> 32);
        return bitset.Slice(checked((int)blockIndex * BytesPerBlock), BytesPerBlock);
    }

    static void ValidateBitset(ReadOnlySpan<byte> bitset)
    {
        if (bitset.Length < BytesPerBlock || bitset.Length % BytesPerBlock != 0)
            throw new ArgumentException("Split-block Bloom-filter bitsets must contain one or more 32-byte blocks.",
                nameof(bitset));
    }
}
