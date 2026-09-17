using System.Buffers.Binary;
using System.Runtime.Intrinsics;

namespace Plank.Reading.Logical.Internal;

static partial class ColumnChunkReader
{
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
