namespace Plank.Reading;

static class DeltaBinaryPackedDecoder
{
    const int BlockSize = 128;
    const int MiniBlockCount = 4;

    internal static int[] ReadInt32(ReadOnlySpan<byte> payload)
    {
        var (values, _) = ReadInt64Core(payload);
        var result = new int[values.Length];
        for (var i = 0; i < values.Length; i++)
            result[i] = checked((int)values[i]);
        return result;
    }

    internal static long[] ReadInt64(ReadOnlySpan<byte> payload)
    {
        var (values, _) = ReadInt64Core(payload);
        return values;
    }

    internal static int ReadConsumedByteCount(ReadOnlySpan<byte> payload)
    {
        var (_, consumedBytes) = ReadInt64Core(payload);
        return consumedBytes;
    }

    static (long[] Values, int ConsumedBytes) ReadInt64Core(ReadOnlySpan<byte> payload)
    {
        var reader = new DeltaBinaryPackedReader(payload);
        var blockSize = checked((int)reader.ReadUnsignedVarInt());
        var miniBlockCount = checked((int)reader.ReadUnsignedVarInt());
        var valueCount = checked((int)reader.ReadUnsignedVarInt());
        if (blockSize != BlockSize || miniBlockCount != MiniBlockCount)
            throw new NotSupportedException(
                $"Delta binary packed decoding currently supports block size {BlockSize} and mini-block count {MiniBlockCount} only.");

        if (valueCount == 0)
        {
            _ = reader.ReadUnsignedVarInt();
            return ([], reader.Offset);
        }

        var values = new long[valueCount];
        values[0] = reader.ReadZigZagInt64();
        var index = 1;
        var previous = values[0];
        var miniBlockSize = blockSize / miniBlockCount;
        Span<byte> bitWidths = stackalloc byte[MiniBlockCount];

        while (index < valueCount)
        {
            var minDelta = reader.ReadZigZagInt64();
            for (var i = 0; i < MiniBlockCount; i++)
                bitWidths[i] = reader.ReadByte();

            for (var miniBlock = 0; miniBlock < MiniBlockCount && index < valueCount; miniBlock++)
            {
                var bitWidth = bitWidths[miniBlock];
                for (var i = 0; i < miniBlockSize && index < valueCount; i++)
                {
                    var delta = bitWidth == 0 ? 0UL : reader.ReadPackedUnsigned(bitWidth);
                    previous = unchecked(previous + minDelta + (long)delta);
                    values[index++] = previous;
                }
            }
        }

        return (values, reader.Offset);
    }
}
