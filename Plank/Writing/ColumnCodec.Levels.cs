namespace Plank.Writing;

static partial class ColumnCodec
{
    interface IOptionalLevelTraits<T>
    {
        static abstract bool IsDefined(T value);
    }

    readonly struct NullableStructLevelTraits<T> : IOptionalLevelTraits<T?> where T : struct
    {
        public static bool IsDefined(T? value)
            => value.HasValue;
    }

    readonly struct NullableReferenceLevelTraits<T> : IOptionalLevelTraits<T?> where T : class
    {
        public static bool IsDefined(T? value)
            => value is not null;
    }

    internal static int GetDefinitionLevelsByteCount(int valueCount)
    {
        if (valueCount == 0)
            return 0;

        var groupCount = (valueCount + 7) >> 3;
        var header = (uint)((groupCount << 1) | 1);
        return GetVarUInt32Length(header) + groupCount;
    }

    static int GetLevelByteCount(int valueCount, int bitWidth)
    {
        if (valueCount == 0)
            return 0;

        var groupCount = (valueCount + 7) >> 3;
        var header = (uint)((groupCount << 1) | 1);
        return checked(GetVarUInt32Length(header) + (groupCount * bitWidth));
    }

    static int WriteDefinitionLevelsCore<T, TTraits>(ReadOnlySpan<T> values, Span<byte> destination)
        where TTraits : struct, IOptionalLevelTraits<T>
    {
        if (values.Length == 0)
            return 0;

        var groupCount = (values.Length + 7) >> 3;
        var header = (uint)((groupCount << 1) | 1);
        var offset = WriteVarUInt32(header, destination);
        for (var group = 0; group < groupCount; group++)
        {
            byte packed = 0;
            var groupStart = group << 3;
            for (var bit = 0; bit < 8; bit++)
            {
                var index = groupStart + bit;
                if (index >= values.Length)
                    break;
                if (TTraits.IsDefined(values[index]))
                    packed |= (byte)(1 << bit);
            }

            destination[offset++] = packed;
        }

        return offset;
    }

    static int WriteDefinitionLevels<T>(ReadOnlySpan<T?> values, Span<byte> destination) where T : struct
        => WriteDefinitionLevelsCore<T?, NullableStructLevelTraits<T>>(values, destination);

    static int WriteDefinitionLevels<T>(ReadOnlySpan<T?> values, Span<byte> destination) where T : class
        => WriteDefinitionLevelsCore<T?, NullableReferenceLevelTraits<T>>(values, destination);

    static void WriteAllDefinedLevels(int valueCount, Span<byte> destination)
    {
        if (valueCount == 0)
            return;

        var groupCount = (valueCount + 7) >> 3;
        var header = (uint)((groupCount << 1) | 1);
        var offset = WriteVarUInt32(header, destination);
        for (var i = 0; i < groupCount; i++)
            destination[offset + i] = 0xFF;
    }

    static int WriteVarUInt32(uint value, Span<byte> destination)
    {
        var offset = 0;
        while (value >= 0x80)
        {
            destination[offset++] = (byte)(value | 0x80);
            value >>= 7;
        }

        destination[offset++] = (byte)value;
        return offset;
    }

    static int GetVarUInt32Length(uint value)
    {
        var length = 1;
        while (value >= 0x80)
        {
            value >>= 7;
            length++;
        }

        return length;
    }

    static int CountRepeatedValues<T>(ReadOnlySpan<T[]> rows, string columnName, out int emptyRowCount)
    {
        emptyRowCount = 0;
        var total = 0;
        foreach (var row in rows)
        {
            if (row is null)
                throw new InvalidOperationException($"Column '{columnName}' does not support null row arrays.");
            if (row.Length == 0)
                emptyRowCount++;
            total = checked(total + row.Length);
        }

        return total;
    }

    static int WriteRepetitionLevels<T>(ReadOnlySpan<T[]> rows, int levelValueCount, Span<byte> destination)
    {
        if (levelValueCount == 0)
            return 0;

        var groupCount = (levelValueCount + 7) >> 3;
        var header = (uint)((groupCount << 1) | 1);
        var offset = WriteVarUInt32(header, destination);
        byte packed = 0;
        var bitIndex = 0;

        foreach (var row in rows)
        {
            if (row.Length == 0)
            {
                bitIndex++;
                if (bitIndex != 8)
                    continue;

                destination[offset++] = packed;
                packed = 0;
                bitIndex = 0;
                continue;
            }

            for (var valueIndex = 0; valueIndex < row.Length; valueIndex++)
            {
                if (valueIndex > 0)
                    packed |= (byte)(1 << bitIndex);

                bitIndex++;
                if (bitIndex != 8)
                    continue;

                destination[offset++] = packed;
                packed = 0;
                bitIndex = 0;
            }
        }

        if (bitIndex > 0)
            destination[offset++] = packed;
        return offset;
    }

    static int WriteRepeatedDefinitionLevels<T>(ReadOnlySpan<T[]> rows, int levelValueCount, Span<byte> destination)
    {
        if (levelValueCount == 0)
            return 0;

        var groupCount = (levelValueCount + 7) >> 3;
        var header = (uint)((groupCount << 1) | 1);
        var offset = WriteVarUInt32(header, destination);
        byte packed = 0;
        var bitIndex = 0;

        foreach (var row in rows)
        {
            if (row.Length == 0)
            {
                bitIndex++;
                if (bitIndex != 8)
                    continue;

                destination[offset++] = packed;
                packed = 0;
                bitIndex = 0;
                continue;
            }

            for (var valueIndex = 0; valueIndex < row.Length; valueIndex++)
            {
                packed |= (byte)(1 << bitIndex);
                bitIndex++;
                if (bitIndex != 8)
                    continue;

                destination[offset++] = packed;
                packed = 0;
                bitIndex = 0;
            }
        }

        if (bitIndex > 0)
            destination[offset++] = packed;
        return offset;
    }

    static int WriteRepeatedDefinitionLevelsOptionalCore<T, TTraits>(ReadOnlySpan<T[]> rows, int levelValueCount,
        Span<byte> destination)
        where TTraits : struct, IOptionalLevelTraits<T>
    {
        if (levelValueCount == 0)
            return 0;

        var groupCount = (levelValueCount + 7) >> 3;
        var header = (uint)((groupCount << 1) | 1);
        var offset = WriteVarUInt32(header, destination);
        ushort packed = 0;
        var indexInGroup = 0;

        foreach (var row in rows)
        {
            if (row.Length == 0)
            {
                indexInGroup++;
                if (indexInGroup != 8)
                    continue;

                destination[offset++] = (byte)packed;
                destination[offset++] = (byte)(packed >> 8);
                packed = 0;
                indexInGroup = 0;
                continue;
            }

            foreach (var t in row)
            {
                var level = TTraits.IsDefined(t) ? 2 : 1;
                packed |= (ushort)(level << (indexInGroup * 2));
                indexInGroup++;
                if (indexInGroup != 8)
                    continue;

                destination[offset++] = (byte)packed;
                destination[offset++] = (byte)(packed >> 8);
                packed = 0;
                indexInGroup = 0;
            }
        }

        if (indexInGroup <= 0) return offset;
        destination[offset++] = (byte)packed;
        destination[offset++] = (byte)(packed >> 8);

        return offset;
    }

    static int WriteRepeatedDefinitionLevelsOptional<T>(ReadOnlySpan<T?[]> rows, int levelValueCount,
        Span<byte> destination) where T : struct
        => WriteRepeatedDefinitionLevelsOptionalCore<T?, NullableStructLevelTraits<T>>(rows, levelValueCount,
            destination);

    static int WriteRepeatedDefinitionLevelsOptional<T>(ReadOnlySpan<T?[]> rows, int levelValueCount,
        Span<byte> destination) where T : class
        => WriteRepeatedDefinitionLevelsOptionalCore<T?, NullableReferenceLevelTraits<T>>(rows, levelValueCount,
            destination);

    static int WriteDefinitionLevels<T>(ReadOnlySpan<T?> values, VariableSizeBuffer writer) where T : struct
    {
        var destination = writer.GetSpan(GetDefinitionLevelsByteCount(values.Length));
        var written = WriteDefinitionLevels(values, destination);
        writer.Advance(written);
        return written;
    }

    static int WriteDefinitionLevels<T>(ReadOnlySpan<T?> values, VariableSizeBuffer writer) where T : class
    {
        var destination = writer.GetSpan(GetDefinitionLevelsByteCount(values.Length));
        var written = WriteDefinitionLevels(values, destination);
        writer.Advance(written);
        return written;
    }

    static int WriteAllDefinedLevels(int valueCount, VariableSizeBuffer writer)
    {
        if (valueCount == 0)
            return 0;

        var byteCount = GetDefinitionLevelsByteCount(valueCount);
        var destination = writer.GetSpan(byteCount);
        WriteAllDefinedLevels(valueCount, destination);
        writer.Advance(byteCount);
        return byteCount;
    }

    static int WriteRepetitionLevels<T>(ReadOnlySpan<T[]> rows, int levelValueCount, VariableSizeBuffer writer)
    {
        var destination = writer.GetSpan(GetDefinitionLevelsByteCount(levelValueCount));
        var written = WriteRepetitionLevels(rows, levelValueCount, destination);
        writer.Advance(written);
        return written;
    }

    static int WriteRepeatedDefinitionLevels<T>(ReadOnlySpan<T[]> rows, int levelValueCount, VariableSizeBuffer writer)
    {
        var destination = writer.GetSpan(GetDefinitionLevelsByteCount(levelValueCount));
        var written = WriteRepeatedDefinitionLevels(rows, levelValueCount, destination);
        writer.Advance(written);
        return written;
    }

    static int WriteRepeatedDefinitionLevelsOptional<T>(ReadOnlySpan<T?[]> rows, int levelValueCount,
        VariableSizeBuffer writer) where T : struct
    {
        var destination = writer.GetSpan(GetLevelByteCount(levelValueCount, 2));
        var written = WriteRepeatedDefinitionLevelsOptional(rows, levelValueCount, destination);
        writer.Advance(written);
        return written;
    }

    static int WriteRepeatedDefinitionLevelsOptional<T>(ReadOnlySpan<T?[]> rows, int levelValueCount,
        VariableSizeBuffer writer) where T : class
    {
        var destination = writer.GetSpan(GetLevelByteCount(levelValueCount, 2));
        var written = WriteRepeatedDefinitionLevelsOptional(rows, levelValueCount, destination);
        writer.Advance(written);
        return written;
    }

}
