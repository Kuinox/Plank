using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Plank.BloomFilters;

static class XxHash64
{
    const ulong Prime1 = 11400714785074694791UL;
    const ulong Prime2 = 14029467366897019727UL;
    const ulong Prime3 = 1609587929392839161UL;
    const ulong Prime4 = 9650029242287828579UL;
    const ulong Prime5 = 2870177450012600261UL;

    internal static ulong Hash(ReadOnlySpan<byte> value)
    {
        unchecked
        {
            var offset = 0;
            ulong hash;
            if (value.Length >= 32)
            {
                var v1 = Prime1 + Prime2;
                var v2 = Prime2;
                ulong v3 = 0;
                var v4 = 0UL - Prime1;
                var limit = value.Length - 32;
                do
                {
                    v1 = Round(v1, BinaryPrimitives.ReadUInt64LittleEndian(value[offset..]));
                    v2 = Round(v2, BinaryPrimitives.ReadUInt64LittleEndian(value[(offset + 8)..]));
                    v3 = Round(v3, BinaryPrimitives.ReadUInt64LittleEndian(value[(offset + 16)..]));
                    v4 = Round(v4, BinaryPrimitives.ReadUInt64LittleEndian(value[(offset + 24)..]));
                    offset += 32;
                } while (offset <= limit);

                hash = BitOperations.RotateLeft(v1, 1) + BitOperations.RotateLeft(v2, 7) +
                       BitOperations.RotateLeft(v3, 12) + BitOperations.RotateLeft(v4, 18);
                hash = MergeRound(hash, v1);
                hash = MergeRound(hash, v2);
                hash = MergeRound(hash, v3);
                hash = MergeRound(hash, v4);
            }
            else
                hash = Prime5;

            hash += (uint)value.Length;
            while (offset <= value.Length - sizeof(ulong))
            {
                var lane = Round(0, BinaryPrimitives.ReadUInt64LittleEndian(value[offset..]));
                hash ^= lane;
                hash = BitOperations.RotateLeft(hash, 27) * Prime1 + Prime4;
                offset += sizeof(ulong);
            }

            if (offset <= value.Length - sizeof(uint))
            {
                hash ^= BinaryPrimitives.ReadUInt32LittleEndian(value[offset..]) * Prime1;
                hash = BitOperations.RotateLeft(hash, 23) * Prime2 + Prime3;
                offset += sizeof(uint);
            }

            while (offset < value.Length)
            {
                hash ^= value[offset] * Prime5;
                hash = BitOperations.RotateLeft(hash, 11) * Prime1;
                offset++;
            }

            hash ^= hash >> 33;
            hash *= Prime2;
            hash ^= hash >> 29;
            hash *= Prime3;
            hash ^= hash >> 32;
            return hash;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static ulong Round(ulong accumulator, ulong lane)
    {
        unchecked
        {
            accumulator += lane * Prime2;
            accumulator = BitOperations.RotateLeft(accumulator, 31);
            return accumulator * Prime1;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static ulong MergeRound(ulong accumulator, ulong lane)
    {
        unchecked
        {
            accumulator ^= Round(0, lane);
            return accumulator * Prime1 + Prime4;
        }
    }
}
