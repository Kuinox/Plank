using System.Buffers.Binary;

namespace Plank.Writing;

static partial class ColumnCodec
{
    delegate void RepeatedValueWrite<T>(ReadOnlySpan<T[]> rows, ColumnBufferWriter writer,
        DateTimeKindHandling dateTimeKindHandling, string columnName);

    interface IRepeatedOptionalReferenceWriter<T> where T : class
    {
        static abstract int GetPayloadLength(T value);
        static abstract void WritePayload(T value, ColumnBufferWriter writer, int payloadLength, string columnName);
    }

    interface IRepeatedOptionalScalarWriter<T> where T : struct
    {
        static abstract void WritePayload(T value, ColumnBufferWriter writer, DateTimeKindHandling dateTimeKindHandling,
            string columnName);
    }

    interface IRepeatedScalarWriter<T>
    {
        static abstract int ValueSize { get; }
        static abstract void Write(T value, Span<byte> destination, ref int offset,
            DateTimeKindHandling dateTimeKindHandling, string columnName);
    }

    readonly struct RepeatedInt32Writer : IRepeatedScalarWriter<int>
    {
        public static int ValueSize => sizeof(int);

        public static void Write(int value, Span<byte> destination, ref int offset,
            DateTimeKindHandling dateTimeKindHandling, string columnName)
        {
            BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(offset, sizeof(int)), value);
            offset += sizeof(int);
        }
    }

    readonly struct RepeatedDateOnlyWriter : IRepeatedScalarWriter<DateOnly>
    {
        public static int ValueSize => sizeof(int);

        public static void Write(DateOnly value, Span<byte> destination, ref int offset,
            DateTimeKindHandling dateTimeKindHandling, string columnName)
        {
            var daysSinceEpoch = checked(value.DayNumber - UnixEpochDayNumber);
            BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(offset, sizeof(int)), daysSinceEpoch);
            offset += sizeof(int);
        }
    }

    readonly struct RepeatedInt64Writer : IRepeatedScalarWriter<long>
    {
        public static int ValueSize => sizeof(long);

        public static void Write(long value, Span<byte> destination, ref int offset,
            DateTimeKindHandling dateTimeKindHandling, string columnName)
        {
            BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(offset, sizeof(long)), value);
            offset += sizeof(long);
        }
    }

    readonly struct RepeatedDateTimeWriter : IRepeatedScalarWriter<DateTime>
    {
        public static int ValueSize => sizeof(long);

        public static void Write(DateTime value, Span<byte> destination, ref int offset,
            DateTimeKindHandling dateTimeKindHandling, string columnName)
        {
            var unixMicros = ToUnixMicroseconds(value, dateTimeKindHandling, columnName);
            BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(offset, sizeof(long)), unixMicros);
            offset += sizeof(long);
        }
    }

    readonly struct RepeatedDateTimeOffsetWriter : IRepeatedScalarWriter<DateTimeOffset>
    {
        public static int ValueSize => sizeof(long);

        public static void Write(DateTimeOffset value, Span<byte> destination, ref int offset,
            DateTimeKindHandling dateTimeKindHandling, string columnName)
        {
            var deltaTicks = checked(value.UtcTicks - UnixEpochTicks);
            var unixMicros = deltaTicks / TicksPerMicrosecond;
            BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(offset, sizeof(long)), unixMicros);
            offset += sizeof(long);
        }
    }

    readonly struct RepeatedTimeOnlyWriter : IRepeatedScalarWriter<TimeOnly>
    {
        public static int ValueSize => sizeof(long);

        public static void Write(TimeOnly value, Span<byte> destination, ref int offset,
            DateTimeKindHandling dateTimeKindHandling, string columnName)
        {
            var micros = value.Ticks / TicksPerMicrosecond;
            BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(offset, sizeof(long)), micros);
            offset += sizeof(long);
        }
    }

    readonly struct RepeatedFloatWriter : IRepeatedScalarWriter<float>
    {
        public static int ValueSize => sizeof(float);

        public static void Write(float value, Span<byte> destination, ref int offset,
            DateTimeKindHandling dateTimeKindHandling, string columnName)
        {
            BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(offset, sizeof(float)),
                BitConverter.SingleToInt32Bits(value));
            offset += sizeof(float);
        }
    }

    readonly struct RepeatedDoubleWriter : IRepeatedScalarWriter<double>
    {
        public static int ValueSize => sizeof(double);

        public static void Write(double value, Span<byte> destination, ref int offset,
            DateTimeKindHandling dateTimeKindHandling, string columnName)
        {
            BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(offset, sizeof(double)),
                BitConverter.DoubleToInt64Bits(value));
            offset += sizeof(double);
        }
    }

    readonly struct RepeatedStringReferenceWriter : IRepeatedOptionalReferenceWriter<string>
    {
        public static int GetPayloadLength(string value)
            => Utf8.GetByteCount(value);

        public static void WritePayload(string value, ColumnBufferWriter writer, int payloadLength, string columnName)
            => WriteStringPayload(writer, value, payloadLength, columnName);
    }

    readonly struct RepeatedByteArrayReferenceWriter : IRepeatedOptionalReferenceWriter<byte[]>
    {
        public static int GetPayloadLength(byte[] value)
            => value.Length;

        public static void WritePayload(byte[] value, ColumnBufferWriter writer, int payloadLength, string columnName)
            => WriteByteArrayPayload(writer, value, payloadLength);
    }

    readonly struct RepeatedOptionalInt32Writer : IRepeatedOptionalScalarWriter<int>
    {
        public static void WritePayload(int value, ColumnBufferWriter writer, DateTimeKindHandling dateTimeKindHandling,
            string columnName)
            => WriteInt32(writer, value);
    }

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

    static void EncodeRepeatedRequired<T, TWriter>(ReadOnlySpan<T[]> rows,
        ref ParquetWriter.RowGroupState.ColumnState state, string columnName, int maxEncodedBytes,
        DateTimeKindHandling dateTimeKindHandling)
        where TWriter : struct, IRepeatedScalarWriter<T>
    {
        static void WriteValues(ReadOnlySpan<T[]> rows, ColumnBufferWriter writer,
            DateTimeKindHandling dateTimeKindHandling, string columnName)
        {
            foreach (var row in rows)
            foreach (var value in row)
            {
                var destination = writer.GetSpan(TWriter.ValueSize);
                var offset = 0;
                TWriter.Write(value, destination, ref offset, dateTimeKindHandling, columnName);
                writer.Advance(offset);
            }
        }

        EncodeRepeatedRequiredCore(rows, ref state, columnName, maxEncodedBytes, dateTimeKindHandling, WriteValues);
    }

    static void EncodeRepeatedRequiredCore<T>(ReadOnlySpan<T[]> rows,
        ref ParquetWriter.RowGroupState.ColumnState state, string columnName, int maxEncodedBytes,
        DateTimeKindHandling dateTimeKindHandling, RepeatedValueWrite<T> writeValues)
    {
        var writer = CreateBufferWriter(ref state, maxEncodedBytes, columnName);
        var dataValueCount = CountRepeatedValues(rows, columnName, out var emptyRowCount);
        var levelValueCount = checked(dataValueCount + emptyRowCount);
        var repetitionByteCount = GetDefinitionLevelsByteCount(levelValueCount);
        var definitionByteCount = GetDefinitionLevelsByteCount(levelValueCount);
        var repetitionWritten = WriteRepetitionLevels(rows, levelValueCount, writer);
        if (repetitionWritten != repetitionByteCount)
            throw new InvalidOperationException($"Column '{columnName}' repetition levels size mismatch.");
        var definitionWritten = WriteRepeatedDefinitionLevels(rows, levelValueCount, writer);
        if (definitionWritten != definitionByteCount)
            throw new InvalidOperationException($"Column '{columnName}' definition levels size mismatch.");
        writeValues(rows, writer, dateTimeKindHandling, columnName);

        SetRepeatedLayout(ref state, rows.Length, levelValueCount, emptyRowCount, writer.WrittenCount, repetitionByteCount,
            definitionByteCount);
    }

    static void EncodeRepeatedInt32(ReadOnlySpan<int[]> rows, ref ParquetWriter.RowGroupState.ColumnState state,
        string columnName, int maxEncodedBytes)
        => EncodeRepeatedRequired<int, RepeatedInt32Writer>(rows, ref state, columnName, maxEncodedBytes,
            DateTimeKindHandling.None);

    static void EncodeRepeatedDateOnly(ReadOnlySpan<DateOnly[]> rows, ref ParquetWriter.RowGroupState.ColumnState state,
        string columnName, int maxEncodedBytes)
        => EncodeRepeatedRequired<DateOnly, RepeatedDateOnlyWriter>(rows, ref state, columnName, maxEncodedBytes,
            DateTimeKindHandling.None);

    static void EncodeRepeatedNullableInt32(ReadOnlySpan<int?[]> rows,
        ref ParquetWriter.RowGroupState.ColumnState state, string columnName, int maxEncodedBytes)
        => EncodeRepeatedOptionalNullableStruct<int, RepeatedOptionalInt32Writer>(rows, ref state, columnName,
            maxEncodedBytes, DateTimeKindHandling.None);

    static void EncodeRepeatedOptionalNullableStruct<T, TWriter>(ReadOnlySpan<T?[]> rows,
        ref ParquetWriter.RowGroupState.ColumnState state, string columnName, int maxEncodedBytes,
        DateTimeKindHandling dateTimeKindHandling)
        where T : struct
        where TWriter : struct, IRepeatedOptionalScalarWriter<T>
    {
        var writer = CreateBufferWriter(ref state, maxEncodedBytes, columnName);
        var elementCount = CountRepeatedValues(rows, columnName, out var emptyRowCount);
        var nonNullCount = 0;
        foreach (var row in rows)
        foreach (var value in row)
            if (value.HasValue)
                nonNullCount++;

        var levelValueCount = checked(elementCount + emptyRowCount);
        var repetitionByteCount = GetDefinitionLevelsByteCount(levelValueCount);
        var definitionByteCount = GetLevelByteCount(levelValueCount, 2);
        var repetitionWritten = WriteRepetitionLevels(rows, levelValueCount, writer);
        if (repetitionWritten != repetitionByteCount)
            throw new InvalidOperationException($"Column '{columnName}' repetition levels size mismatch.");
        var definitionWritten = WriteRepeatedDefinitionLevelsOptional(rows, levelValueCount, writer);
        if (definitionWritten != definitionByteCount)
            throw new InvalidOperationException($"Column '{columnName}' definition levels size mismatch.");

        foreach (var row in rows)
        foreach (var value in row)
        {
            if (!value.HasValue)
                continue;

            TWriter.WritePayload(value.Value, writer, dateTimeKindHandling, columnName);
        }

        var nullCount = checked(levelValueCount - nonNullCount);
        SetRepeatedLayout(ref state, rows.Length, levelValueCount, nullCount, writer.WrittenCount, repetitionByteCount,
            definitionByteCount);
    }

    static void EncodeRepeatedInt64(ReadOnlySpan<long[]> rows, ref ParquetWriter.RowGroupState.ColumnState state,
        string columnName, int maxEncodedBytes)
        => EncodeRepeatedRequired<long, RepeatedInt64Writer>(rows, ref state, columnName, maxEncodedBytes,
            DateTimeKindHandling.None);

    static void EncodeRepeatedDateTime(ReadOnlySpan<DateTime[]> rows, DateTimeKindHandling dateTimeKindHandling,
        ref ParquetWriter.RowGroupState.ColumnState state, string columnName, int maxEncodedBytes)
    {
        ValidateDateTimeHandling(dateTimeKindHandling, columnName);
        EncodeRepeatedRequired<DateTime, RepeatedDateTimeWriter>(rows, ref state, columnName, maxEncodedBytes,
            dateTimeKindHandling);
    }

    static void EncodeRepeatedDateTimeOffset(ReadOnlySpan<DateTimeOffset[]> rows,
        ref ParquetWriter.RowGroupState.ColumnState state, string columnName, int maxEncodedBytes)
        => EncodeRepeatedRequired<DateTimeOffset, RepeatedDateTimeOffsetWriter>(rows, ref state, columnName,
            maxEncodedBytes, DateTimeKindHandling.None);

    static void EncodeRepeatedTimeOnly(ReadOnlySpan<TimeOnly[]> rows, ref ParquetWriter.RowGroupState.ColumnState state,
        string columnName, int maxEncodedBytes)
        => EncodeRepeatedRequired<TimeOnly, RepeatedTimeOnlyWriter>(rows, ref state, columnName, maxEncodedBytes,
            DateTimeKindHandling.None);

    static void EncodeRepeatedFloat(ReadOnlySpan<float[]> rows, ref ParquetWriter.RowGroupState.ColumnState state,
        string columnName, int maxEncodedBytes)
        => EncodeRepeatedRequired<float, RepeatedFloatWriter>(rows, ref state, columnName, maxEncodedBytes,
            DateTimeKindHandling.None);

    static void EncodeRepeatedDouble(ReadOnlySpan<double[]> rows, ref ParquetWriter.RowGroupState.ColumnState state,
        string columnName, int maxEncodedBytes)
        => EncodeRepeatedRequired<double, RepeatedDoubleWriter>(rows, ref state, columnName, maxEncodedBytes,
            DateTimeKindHandling.None);

    static void EncodeRepeatedBoolean(ReadOnlySpan<bool[]> rows, ref ParquetWriter.RowGroupState.ColumnState state,
        string columnName, int maxEncodedBytes)
    {
        static void WriteValues(ReadOnlySpan<bool[]> rows, ColumnBufferWriter writer,
            DateTimeKindHandling dateTimeKindHandling, string columnName)
        {
            var dataValueCount = 0;
            foreach (var row in rows)
                dataValueCount = checked(dataValueCount + row.Length);

            var valueDestination = writer.GetSpan((dataValueCount + 7) >> 3);
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
            writer.Advance((dataValueCount + 7) >> 3);
        }

        EncodeRepeatedRequiredCore(rows, ref state, columnName, maxEncodedBytes, DateTimeKindHandling.None, WriteValues);
    }

    static void EncodeRepeatedString(ReadOnlySpan<string?[]> rows, ref ParquetWriter.RowGroupState.ColumnState state,
        string columnName, int maxEncodedBytes)
        => EncodeRepeatedOptionalReference<string, RepeatedStringReferenceWriter>(rows, ref state, columnName,
            maxEncodedBytes);

    static void EncodeRepeatedByteArray(ReadOnlySpan<byte[]?[]> rows, ref ParquetWriter.RowGroupState.ColumnState state,
        string columnName, int maxEncodedBytes)
        => EncodeRepeatedOptionalReference<byte[], RepeatedByteArrayReferenceWriter>(rows, ref state, columnName,
            maxEncodedBytes);

    static void EncodeRepeatedOptionalReference<T, TWriter>(ReadOnlySpan<T?[]> rows,
        ref ParquetWriter.RowGroupState.ColumnState state, string columnName, int maxEncodedBytes)
        where T : class
        where TWriter : struct, IRepeatedOptionalReferenceWriter<T>
    {
        var writer = CreateBufferWriter(ref state, maxEncodedBytes, columnName);
        var elementCount = CountRepeatedValues(rows, columnName, out var emptyRowCount);
        var nonNullCount = 0;
        foreach (var row in rows)
        foreach (var value in row)
            if (value is not null)
                nonNullCount++;

        var levelValueCount = checked(elementCount + emptyRowCount);
        var repetitionByteCount = GetDefinitionLevelsByteCount(levelValueCount);
        var definitionByteCount = GetLevelByteCount(levelValueCount, 2);

        var repetitionWritten = WriteRepetitionLevels(rows, levelValueCount, writer);
        if (repetitionWritten != repetitionByteCount)
            throw new InvalidOperationException($"Column '{columnName}' repetition levels size mismatch.");
        var definitionWritten = WriteRepeatedDefinitionLevelsOptional(rows, levelValueCount, writer);
        if (definitionWritten != definitionByteCount)
            throw new InvalidOperationException($"Column '{columnName}' definition levels size mismatch.");

        foreach (var row in rows)
        foreach (var value in row)
        {
            if (value is null)
                continue;

            var payloadLength = TWriter.GetPayloadLength(value);
            TWriter.WritePayload(value, writer, payloadLength, columnName);
        }

        var nullCount = checked(levelValueCount - nonNullCount);
        SetRepeatedLayout(ref state, rows.Length, levelValueCount, nullCount, writer.WrittenCount, repetitionByteCount,
            definitionByteCount);
    }
}
