using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Plank.Reading.Logical.Internal;

static partial class ColumnChunkReader
{
    // Eight 9-bit indexes use nine bytes; keep the final byte load separate
    // so the last literal group never reads past the page boundary.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void DecodeNullableInt32DictionaryNineBitPortable(ReadOnlySpan<byte> payload,
        ReadOnlySpan<int> dictionary, Span<int?> destination)
    {
        ref var source = ref MemoryMarshal.GetReference(payload);
        ref var target = ref Unsafe.As<int?, ulong>(ref MemoryMarshal.GetReference(destination));
        ref var dictionaryStart = ref MemoryMarshal.GetReference(dictionary);
        var limit = (uint)dictionary.Length;
        for (var valueIndex = 0; valueIndex < destination.Length; valueIndex += 8)
        {
            var byteIndex = valueIndex / 8 * 9;
            var bits = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteIndex));
            var last = Unsafe.Add(ref source, byteIndex + 8);
            var i0 = (uint)(bits & 511);
            var i1 = (uint)(bits >> 9 & 511);
            var i2 = (uint)(bits >> 18 & 511);
            var i3 = (uint)(bits >> 27 & 511);
            var i4 = (uint)(bits >> 36 & 511);
            var i5 = (uint)(bits >> 45 & 511);
            var i6 = (uint)(bits >> 54 & 511);
            var i7 = (uint)((bits >> 63 | (ulong)last << 1) & 511);
            if (i0 >= limit || i1 >= limit || i2 >= limit || i3 >= limit ||
                i4 >= limit || i5 >= limit || i6 >= limit || i7 >= limit)
            {
                ValidateDictionaryIndex((int)i0, dictionary.Length);
                ValidateDictionaryIndex((int)i1, dictionary.Length);
                ValidateDictionaryIndex((int)i2, dictionary.Length);
                ValidateDictionaryIndex((int)i3, dictionary.Length);
                ValidateDictionaryIndex((int)i4, dictionary.Length);
                ValidateDictionaryIndex((int)i5, dictionary.Length);
                ValidateDictionaryIndex((int)i6, dictionary.Length);
                ValidateDictionaryIndex((int)i7, dictionary.Length);
            }

            Unsafe.Add(ref target, valueIndex) = 1UL |
                (ulong)(uint)Unsafe.Add(ref dictionaryStart, (int)i0) << 32;
            Unsafe.Add(ref target, valueIndex + 1) = 1UL |
                (ulong)(uint)Unsafe.Add(ref dictionaryStart, (int)i1) << 32;
            Unsafe.Add(ref target, valueIndex + 2) = 1UL |
                (ulong)(uint)Unsafe.Add(ref dictionaryStart, (int)i2) << 32;
            Unsafe.Add(ref target, valueIndex + 3) = 1UL |
                (ulong)(uint)Unsafe.Add(ref dictionaryStart, (int)i3) << 32;
            Unsafe.Add(ref target, valueIndex + 4) = 1UL |
                (ulong)(uint)Unsafe.Add(ref dictionaryStart, (int)i4) << 32;
            Unsafe.Add(ref target, valueIndex + 5) = 1UL |
                (ulong)(uint)Unsafe.Add(ref dictionaryStart, (int)i5) << 32;
            Unsafe.Add(ref target, valueIndex + 6) = 1UL |
                (ulong)(uint)Unsafe.Add(ref dictionaryStart, (int)i6) << 32;
            Unsafe.Add(ref target, valueIndex + 7) = 1UL |
                (ulong)(uint)Unsafe.Add(ref dictionaryStart, (int)i7) << 32;
        }
    }

    // Eight 11-bit indexes occupy exactly eleven bytes. Do not load sixteen
    // bytes from the input: the final group may end at the page boundary.
    static void DecodeDictionaryLiteral11BitPortable<T>(ReadOnlySpan<byte> payload,
        ReadOnlySpan<T> dictionary, Span<T> destination)
    {
        var firstShuffle = Vector128.Create(
            (byte)0, 1, 2, 255, 1, 2, 3, 255,
            2, 3, 4, 255, 4, 5, 6, 255);
        var secondShuffle = Vector128.Create(
            (byte)5, 6, 7, 255, 6, 7, 8, 255,
            8, 9, 10, 255, 9, 10, 255, 255);
        // Lane starts are at bits 0,3,6,1 and 4,7,2,5 within their
        // shuffled words. Multiplication aligns them for a uniform shift.
        var firstScale = Vector128.Create(128u, 16u, 2u, 64u);
        var secondScale = Vector128.Create(8u, 1u, 32u, 4u);
        var mask = Vector128.Create(2047u);
        var limit = Vector128.Create((uint)dictionary.Length);
        for (var i = 0; i < destination.Length; i += 8)
        {
            var packed = payload.Slice(i / 8 * 11, 11);
            var low = BinaryPrimitives.ReadUInt64LittleEndian(packed);
            var high = (ulong)BinaryPrimitives.ReadUInt16LittleEndian(packed[8..])
                | (ulong)packed[10] << 16;
            var bytes = Vector128.Create(low, high).AsByte();
            var first = ((Vector128.Shuffle(bytes, firstShuffle).AsUInt32() * firstScale) >> 7) & mask;
            var second = ((Vector128.Shuffle(bytes, secondShuffle).AsUInt32() * secondScale) >> 7) & mask;
            if (Vector128.GreaterThanOrEqualAny(first, limit) || Vector128.GreaterThanOrEqualAny(second, limit))
            {
                for (var lane = 0; lane < 4; lane++)
                {
                    ValidateDictionaryIndex((int)first.GetElement(lane), dictionary.Length);
                    ValidateDictionaryIndex((int)second.GetElement(lane), dictionary.Length);
                }
            }
            destination[i] = dictionary[(int)first.GetElement(0)];
            destination[i + 1] = dictionary[(int)first.GetElement(1)];
            destination[i + 2] = dictionary[(int)first.GetElement(2)];
            destination[i + 3] = dictionary[(int)first.GetElement(3)];
            destination[i + 4] = dictionary[(int)second.GetElement(0)];
            destination[i + 5] = dictionary[(int)second.GetElement(1)];
            destination[i + 6] = dictionary[(int)second.GetElement(2)];
            destination[i + 7] = dictionary[(int)second.GetElement(3)];
        }
    }
}
