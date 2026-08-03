using System.Buffers.Binary;
using System.Numerics;

namespace Plank.Reading;

static class XxHash32
{
    const uint Prime1 = 0x9E3779B1;
    const uint Prime2 = 0x85EBCA77;
    const uint Prime3 = 0xC2B2AE3D;
    const uint Prime4 = 0x27D4EB2F;
    const uint Prime5 = 0x165667B1;

    internal static uint Compute(ReadOnlySpan<byte> source)
    {
        var offset = 0;
        uint hash;
        if (source.Length >= 16)
        {
            var lane1 = unchecked(Prime1 + Prime2);
            var lane2 = Prime2;
            uint lane3 = 0;
            var lane4 = unchecked(0U - Prime1);
            var limit = source.Length - 16;
            do
            {
                lane1 = Round(lane1, BinaryPrimitives.ReadUInt32LittleEndian(source[offset..]));
                lane2 = Round(lane2, BinaryPrimitives.ReadUInt32LittleEndian(source[(offset + 4)..]));
                lane3 = Round(lane3, BinaryPrimitives.ReadUInt32LittleEndian(source[(offset + 8)..]));
                lane4 = Round(lane4, BinaryPrimitives.ReadUInt32LittleEndian(source[(offset + 12)..]));
                offset += 16;
            } while (offset <= limit);

            hash = unchecked(BitOperations.RotateLeft(lane1, 1) + BitOperations.RotateLeft(lane2, 7) +
                BitOperations.RotateLeft(lane3, 12) + BitOperations.RotateLeft(lane4, 18));
        }
        else
        {
            hash = Prime5;
        }

        hash = unchecked(hash + (uint)source.Length);
        while (offset <= source.Length - sizeof(uint))
        {
            hash = unchecked(hash + BinaryPrimitives.ReadUInt32LittleEndian(source[offset..]) * Prime3);
            hash = unchecked(BitOperations.RotateLeft(hash, 17) * Prime4);
            offset += sizeof(uint);
        }

        while (offset < source.Length)
        {
            hash = unchecked(hash + source[offset] * Prime5);
            hash = unchecked(BitOperations.RotateLeft(hash, 11) * Prime1);
            offset++;
        }

        hash ^= hash >> 15;
        hash = unchecked(hash * Prime2);
        hash ^= hash >> 13;
        hash = unchecked(hash * Prime3);
        return hash ^ (hash >> 16);
    }

    static uint Round(uint accumulator, uint lane)
    {
        accumulator = unchecked(accumulator + lane * Prime2);
        accumulator = BitOperations.RotateLeft(accumulator, 13);
        return unchecked(accumulator * Prime1);
    }
}
