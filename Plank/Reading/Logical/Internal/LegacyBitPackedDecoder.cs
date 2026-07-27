namespace Plank.Reading.Logical.Internal;

static class LegacyBitPackedDecoder
{
    internal static int GetByteCount(int valueCount, int bitWidth)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(valueCount);
        ArgumentOutOfRangeException.ThrowIfNegative(bitWidth);
        if (bitWidth > sizeof(int) * 8)
            throw new ArgumentOutOfRangeException(nameof(bitWidth));

        return checked((int)(((long)valueCount * bitWidth + 7) / 8));
    }

    internal static void Decode(ReadOnlySpan<byte> payload, int bitWidth, Span<int> destination)
    {
        var byteCount = GetByteCount(destination.Length, bitWidth);
        if (payload.Length < byteCount)
            throw new CorruptParquetException(
                $"Legacy bit-packed payload ({payload.Length} bytes) is too short to decode " +
                $"{destination.Length} values with bit width {bitWidth}.");

        if (bitWidth == 0)
        {
            destination.Clear();
            return;
        }

        var bitOffset = 0;
        for (var valueIndex = 0; valueIndex < destination.Length; valueIndex++)
        {
            var value = 0;
            var remainingBits = bitWidth;
            while (remainingBits > 0)
            {
                var byteIndex = bitOffset >> 3;
                var bitsAvailable = 8 - (bitOffset & 7);
                var bitsToRead = Math.Min(remainingBits, bitsAvailable);
                var shift = bitsAvailable - bitsToRead;
                var mask = (1 << bitsToRead) - 1;
                value = (value << bitsToRead) | ((payload[byteIndex] >> shift) & mask);
                bitOffset += bitsToRead;
                remainingBits -= bitsToRead;
            }
            destination[valueIndex] = value;
        }
    }

    internal static int CountSetBits(ReadOnlySpan<byte> payload, int valueCount)
    {
        var byteCount = GetByteCount(valueCount, bitWidth: 1);
        if (payload.Length < byteCount)
            throw new CorruptParquetException(
                $"Legacy bit-packed payload ({payload.Length} bytes) is too short to decode {valueCount} values.");

        var count = 0;
        for (var i = 0; i < valueCount; i++)
            count += (payload[i >> 3] >> (7 - (i & 7))) & 1;
        return count;
    }
}
