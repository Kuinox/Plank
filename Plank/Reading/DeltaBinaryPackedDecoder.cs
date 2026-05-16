namespace Plank.Reading;

static class DeltaBinaryPackedDecoder
{
    const uint BlockSize = 128;
    const uint MiniBlockCount = 4;

    internal static int[] ReadInt32(ReadOnlySpan<byte> payload)
    {
        var (values, _) = ReadInt32Core(payload);
        return values;
    }

    internal static long[] ReadInt64(ReadOnlySpan<byte> payload)
    {
        var (values, _) = ReadInt64Core(payload);
        return values;
    }

    internal static int ReadInt32(ReadOnlySpan<byte> payload, Span<int> destination)
        => ReadInt32Core(payload, destination);

    internal static int ReadInt64(ReadOnlySpan<byte> payload, Span<long> destination)
        => ReadInt64Core(payload, destination);

    internal static uint[] ReadUInt32(ReadOnlySpan<byte> payload)
    {
        var (values, _) = ReadInt32Core(payload);
        var result = new uint[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            if (values[i] < 0)
                throw new CorruptParquetException(
                    $"Delta encoded length {values[i]} is negative.");
            result[i] = (uint)values[i];
        }
        return result;
    }

    internal static int ReadConsumedByteCount(ReadOnlySpan<byte> payload)
    {
        var (_, consumedBytes) = ReadInt64Core(payload);
        return consumedBytes;
    }

    static (int[] Values, int ConsumedBytes) ReadInt32Core(ReadOnlySpan<byte> payload)
    {
        var reader = new DeltaBinaryPackedReader(payload);
        var blockSize = ReadHeaderVarUInt32(ref reader, "block size");
        var miniBlockCount = ReadHeaderVarUInt32(ref reader, "mini-block count");
        var valueCount = ReadHeaderVarUInt32(ref reader, "value count");
        if (blockSize != BlockSize || miniBlockCount != MiniBlockCount)
            throw new NotSupportedException(
                $"Delta binary packed decoding currently supports block size {BlockSize} and mini-block count {MiniBlockCount} only.");

        if (valueCount == 0)
        {
            _ = reader.ReadUnsignedVarInt();
            return ([], reader.Offset);
        }

        var values = new int[checked((int)valueCount)];
        // Int32 deltas can be wider than Int32, so reconstruct in Int64 and narrow only when storing.
        var previous = reader.ReadZigZagInt64();
        values[0] = NarrowInt32(previous);
        var index = 1U;
        var miniBlockSize = blockSize / miniBlockCount;
        Span<byte> bitWidths = stackalloc byte[checked((int)MiniBlockCount)];

        while (index < valueCount)
        {
            var minDelta = reader.ReadZigZagInt64();
            for (var i = 0U; i < MiniBlockCount; i++)
                bitWidths[checked((int)i)] = reader.ReadByte();

            for (var miniBlock = 0U; miniBlock < MiniBlockCount; miniBlock++)
            {
                var bitWidth = bitWidths[checked((int)miniBlock)];
                for (var i = 0U; i < miniBlockSize; i++)
                {
                    var delta = bitWidth == 0 ? 0UL : reader.ReadPackedUnsigned(bitWidth);
                    if (index < valueCount)
                    {
                        previous = unchecked(previous + minDelta + (long)delta);
                        values[checked((int)index++)] = NarrowInt32(previous);
                    }
                }
            }
        }

        return (values, reader.Offset);
    }

    static int ReadInt32Core(ReadOnlySpan<byte> payload, Span<int> destination)
    {
        var reader = new DeltaBinaryPackedReader(payload);
        var blockSize = ReadHeaderVarUInt32(ref reader, "block size");
        var miniBlockCount = ReadHeaderVarUInt32(ref reader, "mini-block count");
        var valueCount = ReadHeaderVarUInt32(ref reader, "value count");
        if (blockSize != BlockSize || miniBlockCount != MiniBlockCount)
            throw new NotSupportedException(
                $"Delta binary packed decoding currently supports block size {BlockSize} and mini-block count {MiniBlockCount} only.");
        if ((uint)destination.Length != valueCount)
            throw new CorruptParquetException(
                $"DeltaBinaryPacked encoded value count {valueCount} does not match expected {destination.Length}.");

        if (valueCount == 0)
        {
            _ = reader.ReadUnsignedVarInt();
            return reader.Offset;
        }

        var previous = reader.ReadZigZagInt64();
        destination[0] = NarrowInt32(previous);
        var index = 1U;
        var miniBlockSize = blockSize / miniBlockCount;
        Span<byte> bitWidths = stackalloc byte[checked((int)MiniBlockCount)];

        while (index < valueCount)
        {
            var minDelta = reader.ReadZigZagInt64();
            for (var i = 0U; i < MiniBlockCount; i++)
                bitWidths[checked((int)i)] = reader.ReadByte();

            for (var miniBlock = 0U; miniBlock < MiniBlockCount; miniBlock++)
            {
                var bitWidth = bitWidths[checked((int)miniBlock)];
                for (var i = 0U; i < miniBlockSize; i++)
                {
                    var delta = bitWidth == 0 ? 0UL : reader.ReadPackedUnsigned(bitWidth);
                    if (index < valueCount)
                    {
                        previous = unchecked(previous + minDelta + (long)delta);
                        destination[checked((int)index++)] = NarrowInt32(previous);
                    }
                }
            }
        }

        return reader.Offset;
    }

    static (long[] Values, int ConsumedBytes) ReadInt64Core(ReadOnlySpan<byte> payload)
    {
        var reader = new DeltaBinaryPackedReader(payload);
        var blockSize = ReadHeaderVarUInt32(ref reader, "block size");
        var miniBlockCount = ReadHeaderVarUInt32(ref reader, "mini-block count");
        var valueCount = ReadHeaderVarUInt32(ref reader, "value count");
        if (blockSize != BlockSize || miniBlockCount != MiniBlockCount)
            throw new NotSupportedException(
                $"Delta binary packed decoding currently supports block size {BlockSize} and mini-block count {MiniBlockCount} only.");

        if (valueCount == 0)
        {
            _ = reader.ReadUnsignedVarInt();
            return ([], reader.Offset);
        }

        var values = new long[checked((int)valueCount)];
        values[0] = reader.ReadZigZagInt64();
        var index = 1U;
        var previous = values[0];
        var miniBlockSize = blockSize / miniBlockCount;
        Span<byte> bitWidths = stackalloc byte[checked((int)MiniBlockCount)];

        while (index < valueCount)
        {
            var minDelta = reader.ReadZigZagInt64();
            for (var i = 0U; i < MiniBlockCount; i++)
                bitWidths[checked((int)i)] = reader.ReadByte();

            for (var miniBlock = 0U; miniBlock < MiniBlockCount; miniBlock++)
            {
                var bitWidth = bitWidths[checked((int)miniBlock)];
                for (var i = 0U; i < miniBlockSize; i++)
                {
                    var delta = bitWidth == 0 ? 0UL : reader.ReadPackedUnsigned(bitWidth);
                    if (index < valueCount)
                    {
                        previous = unchecked(previous + minDelta + (long)delta);
                        values[checked((int)index++)] = previous;
                    }
                }
            }
        }

        return (values, reader.Offset);
    }

    static int ReadInt64Core(ReadOnlySpan<byte> payload, Span<long> destination)
    {
        var reader = new DeltaBinaryPackedReader(payload);
        var blockSize = ReadHeaderVarUInt32(ref reader, "block size");
        var miniBlockCount = ReadHeaderVarUInt32(ref reader, "mini-block count");
        var valueCount = ReadHeaderVarUInt32(ref reader, "value count");
        if (blockSize != BlockSize || miniBlockCount != MiniBlockCount)
            throw new NotSupportedException(
                $"Delta binary packed decoding currently supports block size {BlockSize} and mini-block count {MiniBlockCount} only.");
        if ((uint)destination.Length != valueCount)
            throw new CorruptParquetException(
                $"DeltaBinaryPacked encoded value count {valueCount} does not match expected {destination.Length}.");

        if (valueCount == 0)
        {
            _ = reader.ReadUnsignedVarInt();
            return reader.Offset;
        }

        destination[0] = reader.ReadZigZagInt64();
        var index = 1U;
        var previous = destination[0];
        var miniBlockSize = blockSize / miniBlockCount;
        Span<byte> bitWidths = stackalloc byte[checked((int)MiniBlockCount)];

        while (index < valueCount)
        {
            var minDelta = reader.ReadZigZagInt64();
            for (var i = 0U; i < MiniBlockCount; i++)
                bitWidths[checked((int)i)] = reader.ReadByte();

            for (var miniBlock = 0U; miniBlock < MiniBlockCount; miniBlock++)
            {
                var bitWidth = bitWidths[checked((int)miniBlock)];
                for (var i = 0U; i < miniBlockSize; i++)
                {
                    var delta = bitWidth == 0 ? 0UL : reader.ReadPackedUnsigned(bitWidth);
                    if (index < valueCount)
                    {
                        previous = unchecked(previous + minDelta + (long)delta);
                        destination[checked((int)index++)] = previous;
                    }
                }
            }
        }

        return reader.Offset;
    }

    static int NarrowInt32(long value)
    {
        if (value < int.MinValue || value > int.MaxValue)
            throw new CorruptParquetException($"Delta binary packed Int32 value {value} is outside the Int32 range.");

        return (int)value;
    }

    static uint ReadHeaderVarUInt32(ref DeltaBinaryPackedReader reader, string fieldName)
    {
        var value = reader.ReadUnsignedVarInt();
        if (value > uint.MaxValue)
            throw new CorruptParquetException(
                $"Delta binary packed {fieldName} {value} exceeds the supported maximum.");
        return (uint)value;
    }
}
