using System.Buffers.Binary;
namespace Plank.Writing;

static partial class ColumnCodec
{
    static void SetRepeatedLayout(ref ParquetWriter.RowGroupState.ColumnState state, int rowCount, int levelValueCount,
        int nullCount, int totalByteCount, int repetitionByteCount, int definitionByteCount)
    {
        state.RowCount = rowCount;
        state.ValueCount = levelValueCount;
        state.EncodedLength = totalByteCount;
        state.UncompressedLength = totalByteCount;
        state.NullCount = nullCount;
        state.RepetitionLevelsByteLength = repetitionByteCount;
        state.DefinitionLevelsByteLength = definitionByteCount;
    }

    static void EncodeRepeatedInt32(ReadOnlySpan<int[]> rows, ref ParquetWriter.RowGroupState.ColumnState state,
        string columnName, int maxEncodedBytes)
    {
        var dataValueCount = CountRepeatedValues(rows, columnName, out var emptyRowCount);
        var levelValueCount = checked(dataValueCount + emptyRowCount);
        var repetitionByteCount = GetDefinitionLevelsByteCount(levelValueCount);
        var definitionByteCount = GetDefinitionLevelsByteCount(levelValueCount);
        var valuesByteCount = checked(dataValueCount * sizeof(int));
        var totalByteCount = checked(repetitionByteCount + definitionByteCount + valuesByteCount);
        var destination = GetDestination(ref state, totalByteCount);
        if (totalByteCount > 0 && destination.IsEmpty)
            throw new InvalidOperationException(
                $"Column '{columnName}' requires {totalByteCount} bytes but encoded buffer capacity is {maxEncodedBytes}.");

        var repetitionWritten = WriteRepetitionLevels(rows, levelValueCount, destination);
        if (repetitionWritten != repetitionByteCount)
            throw new InvalidOperationException($"Column '{columnName}' repetition levels size mismatch.");
        var definitionWritten =
            WriteRepeatedDefinitionLevels(rows, levelValueCount, destination[repetitionByteCount..]);
        if (definitionWritten != definitionByteCount)
            throw new InvalidOperationException($"Column '{columnName}' definition levels size mismatch.");

        var offset = repetitionByteCount + definitionByteCount;
        foreach (var row in rows)
        foreach (var t in row)
        {
            BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(offset, sizeof(int)), t);
            offset += sizeof(int);
        }

        SetRepeatedLayout(ref state, rows.Length, levelValueCount, emptyRowCount, totalByteCount, repetitionByteCount,
            definitionByteCount);
    }

    static void EncodeRepeatedDateOnly(ReadOnlySpan<DateOnly[]> rows, ref ParquetWriter.RowGroupState.ColumnState state,
        string columnName, int maxEncodedBytes)
    {
        var dataValueCount = CountRepeatedValues(rows, columnName, out var emptyRowCount);
        var levelValueCount = checked(dataValueCount + emptyRowCount);
        var repetitionByteCount = GetDefinitionLevelsByteCount(levelValueCount);
        var definitionByteCount = GetDefinitionLevelsByteCount(levelValueCount);
        var valuesByteCount = checked(dataValueCount * sizeof(int));
        var totalByteCount = checked(repetitionByteCount + definitionByteCount + valuesByteCount);
        var destination = GetDestination(ref state, totalByteCount);
        if (totalByteCount > 0 && destination.IsEmpty)
            throw new InvalidOperationException(
                $"Column '{columnName}' requires {totalByteCount} bytes but encoded buffer capacity is {maxEncodedBytes}.");

        var repetitionWritten = WriteRepetitionLevels(rows, levelValueCount, destination);
        if (repetitionWritten != repetitionByteCount)
            throw new InvalidOperationException($"Column '{columnName}' repetition levels size mismatch.");
        var definitionWritten =
            WriteRepeatedDefinitionLevels(rows, levelValueCount, destination[repetitionByteCount..]);
        if (definitionWritten != definitionByteCount)
            throw new InvalidOperationException($"Column '{columnName}' definition levels size mismatch.");

        var offset = repetitionByteCount + definitionByteCount;
        foreach (var row in rows)
        foreach (var t in row)
        {
            var daysSinceEpoch = checked(t.DayNumber - UnixEpochDayNumber);
            BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(offset, sizeof(int)), daysSinceEpoch);
            offset += sizeof(int);
        }

        SetRepeatedLayout(ref state, rows.Length, levelValueCount, emptyRowCount, totalByteCount, repetitionByteCount,
            definitionByteCount);
    }

    static void EncodeRepeatedNullableInt32(ReadOnlySpan<int?[]> rows,
        ref ParquetWriter.RowGroupState.ColumnState state, string columnName, int maxEncodedBytes)
    {
        var elementCount = 0;
        var emptyRowCount = 0;
        var nonNullCount = 0;
        foreach (var t in rows)
        {
            var row = t ??
                      throw new InvalidOperationException($"Column '{columnName}' does not support null row arrays.");
            if (row.Length == 0)
                emptyRowCount++;
            elementCount = checked(elementCount + row.Length);
            foreach (var t1 in row)
                if (t1.HasValue)
                    nonNullCount++;
        }

        var levelValueCount = checked(elementCount + emptyRowCount);
        var repetitionByteCount = GetDefinitionLevelsByteCount(levelValueCount);
        var definitionByteCount = GetLevelByteCount(levelValueCount, 2);
        var valuesByteCount = checked(nonNullCount * sizeof(int));
        var totalByteCount = checked(repetitionByteCount + definitionByteCount + valuesByteCount);
        var destination = GetDestination(ref state, totalByteCount);
        if (totalByteCount > 0 && destination.IsEmpty)
            throw new InvalidOperationException(
                $"Column '{columnName}' requires {totalByteCount} bytes but encoded buffer capacity is {maxEncodedBytes}.");

        var repetitionWritten = WriteRepetitionLevels(rows, levelValueCount, destination);
        if (repetitionWritten != repetitionByteCount)
            throw new InvalidOperationException($"Column '{columnName}' repetition levels size mismatch.");
        var definitionWritten =
            WriteRepeatedDefinitionLevelsOptionalInt32(rows, levelValueCount, destination[repetitionByteCount..]);
        if (definitionWritten != definitionByteCount)
            throw new InvalidOperationException($"Column '{columnName}' definition levels size mismatch.");

        var offset = repetitionByteCount + definitionByteCount;
        foreach (var row in rows)
        foreach (var value in row)
        {
            if (!value.HasValue)
                continue;

            BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(offset, sizeof(int)), value.Value);
            offset += sizeof(int);
        }

        var nullCount = checked(levelValueCount - nonNullCount);
        SetRepeatedLayout(ref state, rows.Length, levelValueCount, nullCount, totalByteCount, repetitionByteCount,
            definitionByteCount);
    }

    static void EncodeRepeatedInt64(ReadOnlySpan<long[]> rows, ref ParquetWriter.RowGroupState.ColumnState state,
        string columnName, int maxEncodedBytes)
    {
        var dataValueCount = CountRepeatedValues(rows, columnName, out var emptyRowCount);
        var levelValueCount = checked(dataValueCount + emptyRowCount);
        var repetitionByteCount = GetDefinitionLevelsByteCount(levelValueCount);
        var definitionByteCount = GetDefinitionLevelsByteCount(levelValueCount);
        var valuesByteCount = checked(dataValueCount * sizeof(long));
        var totalByteCount = checked(repetitionByteCount + definitionByteCount + valuesByteCount);
        var destination = GetDestination(ref state, totalByteCount);
        if (totalByteCount > 0 && destination.IsEmpty)
            throw new InvalidOperationException(
                $"Column '{columnName}' requires {totalByteCount} bytes but encoded buffer capacity is {maxEncodedBytes}.");

        var repetitionWritten = WriteRepetitionLevels(rows, levelValueCount, destination);
        if (repetitionWritten != repetitionByteCount)
            throw new InvalidOperationException($"Column '{columnName}' repetition levels size mismatch.");
        var definitionWritten =
            WriteRepeatedDefinitionLevels(rows, levelValueCount, destination[repetitionByteCount..]);
        if (definitionWritten != definitionByteCount)
            throw new InvalidOperationException($"Column '{columnName}' definition levels size mismatch.");

        var offset = repetitionByteCount + definitionByteCount;
        foreach (var row in rows)
        foreach (var t in row)
        {
            BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(offset, sizeof(long)), t);
            offset += sizeof(long);
        }

        SetRepeatedLayout(ref state, rows.Length, levelValueCount, emptyRowCount, totalByteCount, repetitionByteCount,
            definitionByteCount);
    }

    static void EncodeRepeatedDateTime(ReadOnlySpan<DateTime[]> rows, DateTimeKindHandling dateTimeKindHandling,
        ref ParquetWriter.RowGroupState.ColumnState state, string columnName, int maxEncodedBytes)
    {
        var dataValueCount = CountRepeatedValues(rows, columnName, out var emptyRowCount);
        var levelValueCount = checked(dataValueCount + emptyRowCount);
        var repetitionByteCount = GetDefinitionLevelsByteCount(levelValueCount);
        var definitionByteCount = GetDefinitionLevelsByteCount(levelValueCount);
        var valuesByteCount = checked(dataValueCount * sizeof(long));
        var totalByteCount = checked(repetitionByteCount + definitionByteCount + valuesByteCount);
        var destination = GetDestination(ref state, totalByteCount);
        if (totalByteCount > 0 && destination.IsEmpty)
            throw new InvalidOperationException(
                $"Column '{columnName}' requires {totalByteCount} bytes but encoded buffer capacity is {maxEncodedBytes}.");

        ValidateDateTimeHandling(dateTimeKindHandling, columnName);
        var repetitionWritten = WriteRepetitionLevels(rows, levelValueCount, destination);
        if (repetitionWritten != repetitionByteCount)
            throw new InvalidOperationException($"Column '{columnName}' repetition levels size mismatch.");
        var definitionWritten =
            WriteRepeatedDefinitionLevels(rows, levelValueCount, destination[repetitionByteCount..]);
        if (definitionWritten != definitionByteCount)
            throw new InvalidOperationException($"Column '{columnName}' definition levels size mismatch.");

        var offset = repetitionByteCount + definitionByteCount;
        foreach (var row in rows)
        foreach (var t in row)
        {
            var unixMicros = ToUnixMicroseconds(t, dateTimeKindHandling, columnName);
            BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(offset, sizeof(long)), unixMicros);
            offset += sizeof(long);
        }

        SetRepeatedLayout(ref state, rows.Length, levelValueCount, emptyRowCount, totalByteCount, repetitionByteCount,
            definitionByteCount);
    }

    static void EncodeRepeatedDateTimeOffset(ReadOnlySpan<DateTimeOffset[]> rows,
        ref ParquetWriter.RowGroupState.ColumnState state, string columnName, int maxEncodedBytes)
    {
        var dataValueCount = CountRepeatedValues(rows, columnName, out var emptyRowCount);
        var levelValueCount = checked(dataValueCount + emptyRowCount);
        var repetitionByteCount = GetDefinitionLevelsByteCount(levelValueCount);
        var definitionByteCount = GetDefinitionLevelsByteCount(levelValueCount);
        var valuesByteCount = checked(dataValueCount * sizeof(long));
        var totalByteCount = checked(repetitionByteCount + definitionByteCount + valuesByteCount);
        var destination = GetDestination(ref state, totalByteCount);
        if (totalByteCount > 0 && destination.IsEmpty)
            throw new InvalidOperationException(
                $"Column '{columnName}' requires {totalByteCount} bytes but encoded buffer capacity is {maxEncodedBytes}.");

        var repetitionWritten = WriteRepetitionLevels(rows, levelValueCount, destination);
        if (repetitionWritten != repetitionByteCount)
            throw new InvalidOperationException($"Column '{columnName}' repetition levels size mismatch.");
        var definitionWritten =
            WriteRepeatedDefinitionLevels(rows, levelValueCount, destination[repetitionByteCount..]);
        if (definitionWritten != definitionByteCount)
            throw new InvalidOperationException($"Column '{columnName}' definition levels size mismatch.");

        var offset = repetitionByteCount + definitionByteCount;
        foreach (var row in rows)
        foreach (var t in row)
        {
            var deltaTicks = checked(t.UtcTicks - UnixEpochTicks);
            var unixMicros = deltaTicks / TicksPerMicrosecond;
            BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(offset, sizeof(long)), unixMicros);
            offset += sizeof(long);
        }

        SetRepeatedLayout(ref state, rows.Length, levelValueCount, emptyRowCount, totalByteCount, repetitionByteCount,
            definitionByteCount);
    }

    static void EncodeRepeatedTimeOnly(ReadOnlySpan<TimeOnly[]> rows, ref ParquetWriter.RowGroupState.ColumnState state,
        string columnName, int maxEncodedBytes)
    {
        var dataValueCount = CountRepeatedValues(rows, columnName, out var emptyRowCount);
        var levelValueCount = checked(dataValueCount + emptyRowCount);
        var repetitionByteCount = GetDefinitionLevelsByteCount(levelValueCount);
        var definitionByteCount = GetDefinitionLevelsByteCount(levelValueCount);
        var valuesByteCount = checked(dataValueCount * sizeof(long));
        var totalByteCount = checked(repetitionByteCount + definitionByteCount + valuesByteCount);
        var destination = GetDestination(ref state, totalByteCount);
        if (totalByteCount > 0 && destination.IsEmpty)
            throw new InvalidOperationException(
                $"Column '{columnName}' requires {totalByteCount} bytes but encoded buffer capacity is {maxEncodedBytes}.");

        var repetitionWritten = WriteRepetitionLevels(rows, levelValueCount, destination);
        if (repetitionWritten != repetitionByteCount)
            throw new InvalidOperationException($"Column '{columnName}' repetition levels size mismatch.");
        var definitionWritten =
            WriteRepeatedDefinitionLevels(rows, levelValueCount, destination[repetitionByteCount..]);
        if (definitionWritten != definitionByteCount)
            throw new InvalidOperationException($"Column '{columnName}' definition levels size mismatch.");

        var offset = repetitionByteCount + definitionByteCount;
        foreach (var row in rows)
        foreach (var t in row)
        {
            var micros = t.Ticks / TicksPerMicrosecond;
            BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(offset, sizeof(long)), micros);
            offset += sizeof(long);
        }

        SetRepeatedLayout(ref state, rows.Length, levelValueCount, emptyRowCount, totalByteCount, repetitionByteCount,
            definitionByteCount);
    }

    static void EncodeRepeatedFloat(ReadOnlySpan<float[]> rows, ref ParquetWriter.RowGroupState.ColumnState state,
        string columnName, int maxEncodedBytes)
    {
        var dataValueCount = CountRepeatedValues(rows, columnName, out var emptyRowCount);
        var levelValueCount = checked(dataValueCount + emptyRowCount);
        var repetitionByteCount = GetDefinitionLevelsByteCount(levelValueCount);
        var definitionByteCount = GetDefinitionLevelsByteCount(levelValueCount);
        var valuesByteCount = checked(dataValueCount * sizeof(float));
        var totalByteCount = checked(repetitionByteCount + definitionByteCount + valuesByteCount);
        var destination = GetDestination(ref state, totalByteCount);
        if (totalByteCount > 0 && destination.IsEmpty)
            throw new InvalidOperationException(
                $"Column '{columnName}' requires {totalByteCount} bytes but encoded buffer capacity is {maxEncodedBytes}.");

        var repetitionWritten = WriteRepetitionLevels(rows, levelValueCount, destination);
        if (repetitionWritten != repetitionByteCount)
            throw new InvalidOperationException($"Column '{columnName}' repetition levels size mismatch.");
        var definitionWritten =
            WriteRepeatedDefinitionLevels(rows, levelValueCount, destination[repetitionByteCount..]);
        if (definitionWritten != definitionByteCount)
            throw new InvalidOperationException($"Column '{columnName}' definition levels size mismatch.");

        var offset = repetitionByteCount + definitionByteCount;
        foreach (var row in rows)
        foreach (var t in row)
        {
            BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(offset, sizeof(float)),
                BitConverter.SingleToInt32Bits(t));
            offset += sizeof(float);
        }

        SetRepeatedLayout(ref state, rows.Length, levelValueCount, emptyRowCount, totalByteCount, repetitionByteCount,
            definitionByteCount);
    }

    static void EncodeRepeatedDouble(ReadOnlySpan<double[]> rows, ref ParquetWriter.RowGroupState.ColumnState state,
        string columnName, int maxEncodedBytes)
    {
        var dataValueCount = CountRepeatedValues(rows, columnName, out var emptyRowCount);
        var levelValueCount = checked(dataValueCount + emptyRowCount);
        var repetitionByteCount = GetDefinitionLevelsByteCount(levelValueCount);
        var definitionByteCount = GetDefinitionLevelsByteCount(levelValueCount);
        var valuesByteCount = checked(dataValueCount * sizeof(double));
        var totalByteCount = checked(repetitionByteCount + definitionByteCount + valuesByteCount);
        var destination = GetDestination(ref state, totalByteCount);
        if (totalByteCount > 0 && destination.IsEmpty)
            throw new InvalidOperationException(
                $"Column '{columnName}' requires {totalByteCount} bytes but encoded buffer capacity is {maxEncodedBytes}.");

        var repetitionWritten = WriteRepetitionLevels(rows, levelValueCount, destination);
        if (repetitionWritten != repetitionByteCount)
            throw new InvalidOperationException($"Column '{columnName}' repetition levels size mismatch.");
        var definitionWritten =
            WriteRepeatedDefinitionLevels(rows, levelValueCount, destination[repetitionByteCount..]);
        if (definitionWritten != definitionByteCount)
            throw new InvalidOperationException($"Column '{columnName}' definition levels size mismatch.");

        var offset = repetitionByteCount + definitionByteCount;
        foreach (var row in rows)
        foreach (var t in row)
        {
            BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(offset, sizeof(double)),
                BitConverter.DoubleToInt64Bits(t));
            offset += sizeof(double);
        }

        SetRepeatedLayout(ref state, rows.Length, levelValueCount, emptyRowCount, totalByteCount, repetitionByteCount,
            definitionByteCount);
    }

    static void EncodeRepeatedBoolean(ReadOnlySpan<bool[]> rows, ref ParquetWriter.RowGroupState.ColumnState state,
        string columnName, int maxEncodedBytes)
    {
        var dataValueCount = CountRepeatedValues(rows, columnName, out var emptyRowCount);
        var levelValueCount = checked(dataValueCount + emptyRowCount);
        var repetitionByteCount = GetDefinitionLevelsByteCount(levelValueCount);
        var definitionByteCount = GetDefinitionLevelsByteCount(levelValueCount);
        var valuesByteCount = (dataValueCount + 7) >> 3;
        var totalByteCount = checked(repetitionByteCount + definitionByteCount + valuesByteCount);
        var destination = GetDestination(ref state, totalByteCount);
        if (totalByteCount > 0 && destination.IsEmpty)
            throw new InvalidOperationException(
                $"Column '{columnName}' requires {totalByteCount} bytes but encoded buffer capacity is {maxEncodedBytes}.");

        var repetitionWritten = WriteRepetitionLevels(rows, levelValueCount, destination);
        if (repetitionWritten != repetitionByteCount)
            throw new InvalidOperationException($"Column '{columnName}' repetition levels size mismatch.");
        var definitionWritten =
            WriteRepeatedDefinitionLevels(rows, levelValueCount, destination[repetitionByteCount..]);
        if (definitionWritten != definitionByteCount)
            throw new InvalidOperationException($"Column '{columnName}' definition levels size mismatch.");

        var valueDestination = destination[(repetitionByteCount + definitionByteCount)..];
        valueDestination.Clear();
        var flattenedIndex = 0;
        foreach (var row in rows)
        foreach (var t in row)
        {
            if (t)
            {
                var byteIndex = flattenedIndex >> 3;
                var bitIndex = flattenedIndex & 7;
                valueDestination[byteIndex] |= (byte)(1 << bitIndex);
            }

            flattenedIndex++;
        }

        SetRepeatedLayout(ref state, rows.Length, levelValueCount, emptyRowCount, totalByteCount, repetitionByteCount,
            definitionByteCount);
    }

    static void EncodeRepeatedString(ReadOnlySpan<string?[]> rows, ref ParquetWriter.RowGroupState.ColumnState state,
        string columnName, int maxEncodedBytes)
    {
        var elementCount = 0;
        var emptyRowCount = 0;
        var nonNullCount = 0;
        var valuesByteCount = 0;
        foreach (var t in rows)
        {
            var row = t ??
                      throw new InvalidOperationException($"Column '{columnName}' does not support null row arrays.");
            if (row.Length == 0)
                emptyRowCount++;
            elementCount = checked(elementCount + row.Length);
            foreach (var value in row)
            {
                if (value is null)
                    continue;

                nonNullCount++;
                valuesByteCount = checked(valuesByteCount + sizeof(int));
                valuesByteCount = checked(valuesByteCount + Utf8.GetByteCount(value));
            }
        }

        var levelValueCount = checked(elementCount + emptyRowCount);
        var repetitionByteCount = GetDefinitionLevelsByteCount(levelValueCount);
        var definitionByteCount = GetLevelByteCount(levelValueCount, 2);
        var totalByteCount = checked(repetitionByteCount + definitionByteCount + valuesByteCount);
        var destination = GetDestination(ref state, totalByteCount);
        if (totalByteCount > 0 && destination.IsEmpty)
            throw new InvalidOperationException(
                $"Column '{columnName}' requires {totalByteCount} bytes but encoded buffer capacity is {maxEncodedBytes}.");

        var repetitionWritten = WriteRepetitionLevels(rows, levelValueCount, destination);
        if (repetitionWritten != repetitionByteCount)
            throw new InvalidOperationException($"Column '{columnName}' repetition levels size mismatch.");
        var definitionWritten =
            WriteRepeatedDefinitionLevelsOptionalString(rows, levelValueCount, destination[repetitionByteCount..]);
        if (definitionWritten != definitionByteCount)
            throw new InvalidOperationException($"Column '{columnName}' definition levels size mismatch.");

        var offset = repetitionByteCount + definitionByteCount;
        foreach (var row in rows)
        foreach (var value in row)
        {
            if (value is null)
                continue;

            var utf8Length = Utf8.GetByteCount(value);
            BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(offset, sizeof(int)), utf8Length);
            offset += sizeof(int);
            if (utf8Length == 0)
                continue;

            var written = Utf8.GetBytes(value.AsSpan(), destination.Slice(offset, utf8Length));
            if (written != utf8Length)
                throw new InvalidOperationException($"Column '{columnName}' could not encode UTF-8 payload.");
            offset += utf8Length;
        }

        var nullCount = checked(levelValueCount - nonNullCount);
        SetRepeatedLayout(ref state, rows.Length, levelValueCount, nullCount, totalByteCount, repetitionByteCount,
            definitionByteCount);
    }

    static void EncodeRepeatedByteArray(ReadOnlySpan<byte[]?[]> rows, ref ParquetWriter.RowGroupState.ColumnState state,
        string columnName, int maxEncodedBytes)
    {
        var elementCount = 0;
        var emptyRowCount = 0;
        var nonNullCount = 0;
        var valuesByteCount = 0;
        foreach (var t in rows)
        {
            var row = t ??
                      throw new InvalidOperationException($"Column '{columnName}' does not support null row arrays.");
            if (row.Length == 0)
                emptyRowCount++;
            elementCount = checked(elementCount + row.Length);
            foreach (var value in row)
            {
                if (value is null)
                    continue;

                nonNullCount++;
                valuesByteCount = checked(valuesByteCount + sizeof(int));
                valuesByteCount = checked(valuesByteCount + value.Length);
            }
        }

        var levelValueCount = checked(elementCount + emptyRowCount);
        var repetitionByteCount = GetDefinitionLevelsByteCount(levelValueCount);
        var definitionByteCount = GetLevelByteCount(levelValueCount, 2);
        var totalByteCount = checked(repetitionByteCount + definitionByteCount + valuesByteCount);
        var destination = GetDestination(ref state, totalByteCount);
        if (totalByteCount > 0 && destination.IsEmpty)
            throw new InvalidOperationException(
                $"Column '{columnName}' requires {totalByteCount} bytes but encoded buffer capacity is {maxEncodedBytes}.");

        var repetitionWritten = WriteRepetitionLevels(rows, levelValueCount, destination);
        if (repetitionWritten != repetitionByteCount)
            throw new InvalidOperationException($"Column '{columnName}' repetition levels size mismatch.");
        var definitionWritten =
            WriteRepeatedDefinitionLevelsOptionalByteArray(rows, levelValueCount, destination[repetitionByteCount..]);
        if (definitionWritten != definitionByteCount)
            throw new InvalidOperationException($"Column '{columnName}' definition levels size mismatch.");

        var offset = repetitionByteCount + definitionByteCount;
        foreach (var row in rows)
        foreach (var value in row)
        {
            if (value is null)
                continue;

            BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(offset, sizeof(int)), value.Length);
            offset += sizeof(int);
            if (value.Length == 0)
                continue;

            value.AsSpan().CopyTo(destination[offset..]);
            offset += value.Length;
        }

        var nullCount = checked(levelValueCount - nonNullCount);
        SetRepeatedLayout(ref state, rows.Length, levelValueCount, nullCount, totalByteCount, repetitionByteCount,
            definitionByteCount);
    }
}
