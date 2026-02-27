using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Plank.Writing.Encoding;

/// <summary>
/// wyhash for arbitrary byte spans.
/// Processes 4 or 8 bytes at a time using overlapping reads and BigMul (MULQ on x86-64).
/// For 7-16 byte keys: 1-2 BigMul operations (~10-12 cycles) vs FNV-1a's sequential chain (~36-48 cycles).
/// </summary>
static class WyHashing
{
    const ulong P0 = 0xa0761d6478bd642fUL;
    const ulong P1 = 0xe7037ed1a0b428dbUL;
    const ulong P2 = 0x8ebc6af09c88c6e3UL;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static ulong Mix(ulong a, ulong b)
    {
        ulong hi = Math.BigMul(a, b, out ulong lo);
        return hi ^ lo;
    }

    // Full-avalanche 32-bit finalizer. Required because BigMul with a small seed concentrates
    // variation in high bits. The Murmur3-style finalize spreads variation into all 32 bits.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int Finalize(ulong h)
    {
        uint f = (uint)(h ^ (h >> 32));
        f ^= f >> 16;
        f *= 0x45d9f3bu;
        f ^= f >> 16;
        return (int)f;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Hash(ReadOnlySpan<byte> data)
    {
        unchecked
        {
            ref byte p = ref MemoryMarshal.GetReference(data);
            ulong seed = (ulong)data.Length;

            if (data.Length <= 3)
            {
                ulong v = data.Length > 0 ? p : 0u;
                if (data.Length > 1) v |= (ulong)Unsafe.Add(ref p, 1) << 8;
                if (data.Length > 2) v |= (ulong)Unsafe.Add(ref p, 2) << 16;
                return Finalize(Mix(seed ^ P0, v ^ P1));
            }

            if (data.Length <= 8)
            {
                ulong a = Unsafe.ReadUnaligned<uint>(ref p);
                ulong b = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref p, data.Length - 4));
                return Finalize(Mix(seed ^ P0, (a | (b << 32)) ^ P1));
            }

            if (data.Length <= 16)
            {
                ulong a = Unsafe.ReadUnaligned<ulong>(ref p);
                ulong b = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref p, data.Length - 8));
                return Finalize(Mix(seed ^ P0, a ^ P1) ^ Mix(seed, b ^ P2));
            }

            ulong acc = seed;
            int i = 0;
            while (i + 16 <= data.Length)
            {
                ulong la = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref p, i));
                ulong lb = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref p, i + 8));
                acc ^= Mix(acc ^ P0, la ^ P1) ^ Mix(acc, lb ^ P2);
                i += 16;
            }
            int tail = data.Length - i;
            if (tail > 0)
            {
                if (tail <= 8)
                {
                    ulong ta = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref p, i));
                    ulong tb = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref p, data.Length - 4));
                    acc ^= Mix(acc ^ P0, (ta | (tb << 32)) ^ P1);
                }
                else
                {
                    ulong ta = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref p, i));
                    ulong tb = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref p, data.Length - 8));
                    acc ^= Mix(acc ^ P0, ta ^ P1) ^ Mix(acc, tb ^ P2);
                }
            }
            return Finalize(acc);
        }
    }
}
