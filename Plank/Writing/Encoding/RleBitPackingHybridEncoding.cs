namespace Plank.Writing;

static class RleBitPackingHybridEncoding
{
    internal static void WriteWithBitWidthPrefix(ReadOnlySpan<int> values, int bitWidth, ref BufferWriter writer)
    {
        if ((uint)bitWidth > 32)
            throw new ArgumentOutOfRangeException(nameof(bitWidth), bitWidth, "Bit width must be between 0 and 32.");

        var header = writer.GetSpan(1);
        header[0] = (byte)bitWidth;
        writer.Advance(1);
        Write(values, bitWidth, ref writer);
    }

    internal static void Write(ReadOnlySpan<int> values, int bitWidth, ref BufferWriter writer)
    {
        if ((uint)bitWidth > 32)
            throw new ArgumentOutOfRangeException(nameof(bitWidth), bitWidth, "Bit width must be between 0 and 32.");
        if (values.Length == 0)
            return;

        var index = 0;
        while (index < values.Length)
        {
            var runLength = CountRunLength(values, index);
            if (runLength >= 8)
            {
                WriteRleRun(values[index], runLength, bitWidth, ref writer);
                index += runLength;
                continue;
            }

            var literalStart = index;
            index += runLength;

            while (index < values.Length)
            {
                runLength = CountRunLength(values, index);
                if (runLength >= 8)
                    break;
                index += runLength;
            }

            WriteBitPackedRun(values[literalStart..index], bitWidth, ref writer);
        }
    }

    internal static int GetBitWidthFromMaxValue(int maxValue)
    {
        if (maxValue < 0)
            throw new ArgumentOutOfRangeException(nameof(maxValue), maxValue, "Maximum value must be non-negative.");
        if (maxValue == 0)
            return 0;

        var width = 0;
        var value = (uint)maxValue;
        while (value != 0)
        {
            width++;
            value >>= 1;
        }

        return width;
    }

    static int CountRunLength(ReadOnlySpan<int> values, int start)
    {
        var value = values[start];
        var length = 1;
        while (start + length < values.Length && values[start + length] == value)
            length++;
        return length;
    }

    static void WriteRleRun(int value, int runLength, int bitWidth, ref BufferWriter writer)
    {
        if (runLength <= 0)
            throw new ArgumentOutOfRangeException(nameof(runLength), runLength, "Run length must be positive.");

        WriteUnsignedVarInt(((uint)runLength) << 1, ref writer);

        var byteWidth = (bitWidth + 7) >> 3;
        if (byteWidth == 0)
            return;

        var unsignedValue = ValidateAndNormalizeValue(value, bitWidth);
        var encoded = writer.GetSpan(byteWidth);
        for (var i = 0; i < byteWidth; i++)
            encoded[i] = (byte)(unsignedValue >> (i * 8));
        writer.Advance(byteWidth);
    }

    static void WriteBitPackedRun(ReadOnlySpan<int> literals, int bitWidth, ref BufferWriter writer)
    {
        if (literals.Length == 0)
            return;

        if (bitWidth == 0)
        {
            WriteRleRun(0, literals.Length, 0, ref writer);
            return;
        }

        var groupCount = (literals.Length + 7) >> 3;
        WriteUnsignedVarInt((((uint)groupCount) << 1) | 1u, ref writer);

        var byteCount = checked(groupCount * bitWidth);
        var destination = writer.GetSpan(byteCount);
        var mask = bitWidth == 32 ? uint.MaxValue : (1u << bitWidth) - 1u;
        ulong bitBuffer = 0;
        var bufferedBits = 0;
        var outputOffset = 0;
        var totalLiterals = groupCount * 8;
        for (var i = 0; i < totalLiterals; i++)
        {
            var value = i < literals.Length ? ValidateAndNormalizeValue(literals[i], bitWidth) : 0u;
            bitBuffer |= ((ulong)value & mask) << bufferedBits;
            bufferedBits += bitWidth;

            while (bufferedBits >= 8)
            {
                destination[outputOffset++] = (byte)bitBuffer;
                bitBuffer >>= 8;
                bufferedBits -= 8;
            }
        }

        if (bufferedBits > 0)
            destination[outputOffset++] = (byte)bitBuffer;

        writer.Advance(outputOffset);
    }

    static uint ValidateAndNormalizeValue(int value, int bitWidth)
    {
        if (bitWidth == 32)
            return unchecked((uint)value);

        if (value < 0)
            throw new InvalidOperationException($"Value '{value}' cannot be encoded with bit width {bitWidth}.");

        var unsignedValue = (uint)value;
        if (bitWidth == 0)
        {
            if (unsignedValue != 0)
                throw new InvalidOperationException("Non-zero value cannot be encoded with bit width 0.");
            return 0;
        }

        var maxValue = (1u << bitWidth) - 1u;
        if (unsignedValue > maxValue)
            throw new InvalidOperationException(
                $"Value '{value}' cannot be encoded with bit width {bitWidth}. Max is {maxValue}.");

        return unsignedValue;
    }

    static void WriteUnsignedVarInt(uint value, ref BufferWriter writer)
    {
        Span<byte> buffer = stackalloc byte[5];
        var offset = 0;
        while (value >= 0x80)
        {
            buffer[offset++] = (byte)(value | 0x80);
            value >>= 7;
        }

        buffer[offset++] = (byte)value;
        var destination = writer.GetSpan(offset);
        buffer[..offset].CopyTo(destination);
        writer.Advance(offset);
    }
}
