using Plank.Schema;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Plank.Writing;

static partial class ColumnCodec
{
    interface IOptionalNullableWriter<T>
        where T : struct
    {
        static abstract int ValueSize { get; }
        static abstract void Write(T value, Span<byte> destination, ref int offset,
            DateTimeKindHandling dateTimeKindHandling, string columnName);
    }

    readonly struct OptionalInt32NullableWriter : IOptionalNullableWriter<int>
    {
        public static int ValueSize => sizeof(int);

        public static void Write(int value, Span<byte> destination, ref int offset,
            DateTimeKindHandling dateTimeKindHandling, string columnName)
        {
            BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(offset, sizeof(int)), value);
            offset += sizeof(int);
        }
    }

    readonly struct OptionalInt64NullableWriter : IOptionalNullableWriter<long>
    {
        public static int ValueSize => sizeof(long);

        public static void Write(long value, Span<byte> destination, ref int offset,
            DateTimeKindHandling dateTimeKindHandling, string columnName)
        {
            BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(offset, sizeof(long)), value);
            offset += sizeof(long);
        }
    }

    readonly struct OptionalDoubleNullableWriter : IOptionalNullableWriter<double>
    {
        public static int ValueSize => sizeof(double);

        public static void Write(double value, Span<byte> destination, ref int offset,
            DateTimeKindHandling dateTimeKindHandling, string columnName)
        {
            var bits = BitConverter.DoubleToInt64Bits(value);
            BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(offset, sizeof(long)), bits);
            offset += sizeof(double);
        }
    }

    readonly struct OptionalDateTimeNullableWriter : IOptionalNullableWriter<DateTime>
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

    static bool TryEncodeOptional<T>(Column column, ReadOnlySpan<T> values, ColumnDispatch.DispatchKey dispatchKey,
        int encodedBufferCapacity, DateTimeKindHandling dateTimeKindHandling,
        ref ParquetWriter.RowGroupState.ColumnState state)
    {
        switch (dispatchKey)
        {
            case ColumnDispatch.DispatchKey.Int32Int32:
                EncodeOptionalInt32AllDefined(Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<int>>(ref values), ref state,
                    column.Name, encodedBufferCapacity);
                return true;
            case ColumnDispatch.DispatchKey.Int32NullableInt32:
                EncodeOptionalInt32(Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<int?>>(ref values), ref state, column.Name,
                    encodedBufferCapacity);
                return true;
            case ColumnDispatch.DispatchKey.Int64Int64:
                EncodeOptionalInt64AllDefined(Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<long>>(ref values), ref state,
                    column.Name, encodedBufferCapacity);
                return true;
            case ColumnDispatch.DispatchKey.Int64NullableInt64:
                EncodeOptionalInt64(Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<long?>>(ref values), ref state, column.Name,
                    encodedBufferCapacity);
                return true;
            case ColumnDispatch.DispatchKey.Int64DateTime:
                EncodeOptionalDateTimeAllDefined(Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<DateTime>>(ref values),
                    dateTimeKindHandling, ref state, column.Name, encodedBufferCapacity);
                return true;
            case ColumnDispatch.DispatchKey.Int64NullableDateTime:
                EncodeOptionalDateTime(Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<DateTime?>>(ref values),
                    dateTimeKindHandling, ref state, column.Name, encodedBufferCapacity);
                return true;
            case ColumnDispatch.DispatchKey.ByteArrayString:
                EncodeOptionalString(Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<string?>>(ref values), ref state,
                    column.Name, encodedBufferCapacity);
                return true;
            case ColumnDispatch.DispatchKey.ByteArrayByteArray:
                EncodeOptionalByteArray(Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<byte[]?>>(ref values), ref state,
                    column.Name, encodedBufferCapacity);
                return true;
            case ColumnDispatch.DispatchKey.DoubleDouble:
                EncodeOptionalDoubleAllDefined(Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<double>>(ref values), ref state,
                    column.Name, encodedBufferCapacity);
                return true;
            case ColumnDispatch.DispatchKey.DoubleNullableDouble:
                EncodeOptionalDouble(Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<double?>>(ref values), ref state,
                    column.Name, encodedBufferCapacity);
                return true;
        }
        return false;
    }

    static void EncodeOptionalNullable<T, TWriter>(ReadOnlySpan<T?> values,
        ref ParquetWriter.RowGroupState.ColumnState state, string columnName, int maxEncodedBytes,
        DateTimeKindHandling dateTimeKindHandling)
        where T : struct
        where TWriter : struct, IOptionalNullableWriter<T>
    {
        var writer = CreateBufferWriter(ref state, maxEncodedBytes, columnName);
        var nonNullCount = 0;
        foreach (var t in values)
            if (t.HasValue)
                nonNullCount++;

        var definitionByteCount = GetDefinitionLevelsByteCount(values.Length);
        var nullCount = values.Length - nonNullCount;
        var writtenDefinitionBytes = WriteDefinitionLevels(values, writer);
        if (writtenDefinitionBytes != definitionByteCount)
            throw new InvalidOperationException($"Column '{columnName}' definition levels size mismatch.");

        foreach (var value in values)
        {
            if (!value.HasValue)
                continue;

            var destination = writer.GetSpan(TWriter.ValueSize);
            var valueOffset = 0;
            TWriter.Write(value.Value, destination, ref valueOffset, dateTimeKindHandling, columnName);
            writer.Advance(valueOffset);
        }

        SetOptionalLayout(ref state, writer.WrittenCount, nullCount, definitionByteCount);
    }

    static void EncodeOptionalInt32(ReadOnlySpan<int?> values, ref ParquetWriter.RowGroupState.ColumnState state,
        string columnName, int maxEncodedBytes)
        => EncodeOptionalNullable<int, OptionalInt32NullableWriter>(values, ref state, columnName, maxEncodedBytes,
            DateTimeKindHandling.None);

    static void EncodeOptionalInt32AllDefined(ReadOnlySpan<int> values,
        ref ParquetWriter.RowGroupState.ColumnState state, string columnName, int maxEncodedBytes)
    {
        var writer = CreateBufferWriter(ref state, maxEncodedBytes, columnName);
        var definitionByteCount = WriteAllDefinedLevels(values.Length, writer);
        var valueDestination = writer.GetSpan(checked(values.Length * sizeof(int)));
        if (BitConverter.IsLittleEndian)
            MemoryMarshal.AsBytes(values).CopyTo(valueDestination);
        else
            for (var i = 0; i < values.Length; i++)
                BinaryPrimitives.WriteInt32LittleEndian(valueDestination.Slice(i * 4, 4), values[i]);
        writer.Advance(checked(values.Length * sizeof(int)));

        SetOptionalLayout(ref state, writer.WrittenCount, 0, definitionByteCount);
    }

    static void EncodeOptionalInt64(ReadOnlySpan<long?> values, ref ParquetWriter.RowGroupState.ColumnState state,
        string columnName, int maxEncodedBytes)
        => EncodeOptionalNullable<long, OptionalInt64NullableWriter>(values, ref state, columnName, maxEncodedBytes,
            DateTimeKindHandling.None);

    static void EncodeOptionalInt64AllDefined(ReadOnlySpan<long> values,
        ref ParquetWriter.RowGroupState.ColumnState state, string columnName, int maxEncodedBytes)
    {
        var writer = CreateBufferWriter(ref state, maxEncodedBytes, columnName);
        var definitionByteCount = WriteAllDefinedLevels(values.Length, writer);
        var valueDestination = writer.GetSpan(checked(values.Length * sizeof(long)));
        if (BitConverter.IsLittleEndian)
            MemoryMarshal.AsBytes(values).CopyTo(valueDestination);
        else
            for (var i = 0; i < values.Length; i++)
                BinaryPrimitives.WriteInt64LittleEndian(valueDestination.Slice(i * 8, 8), values[i]);
        writer.Advance(checked(values.Length * sizeof(long)));

        SetOptionalLayout(ref state, writer.WrittenCount, 0, definitionByteCount);
    }

    static void EncodeOptionalDouble(ReadOnlySpan<double?> values, ref ParquetWriter.RowGroupState.ColumnState state,
        string columnName, int maxEncodedBytes)
        => EncodeOptionalNullable<double, OptionalDoubleNullableWriter>(values, ref state, columnName, maxEncodedBytes,
            DateTimeKindHandling.None);

    static void EncodeOptionalDoubleAllDefined(ReadOnlySpan<double> values,
        ref ParquetWriter.RowGroupState.ColumnState state, string columnName, int maxEncodedBytes)
    {
        var writer = CreateBufferWriter(ref state, maxEncodedBytes, columnName);
        var definitionByteCount = WriteAllDefinedLevels(values.Length, writer);
        var valueDestination = writer.GetSpan(checked(values.Length * sizeof(double)));
        if (BitConverter.IsLittleEndian)
            MemoryMarshal.AsBytes(values).CopyTo(valueDestination);
        else
            for (var i = 0; i < values.Length; i++)
            {
                var bits = BitConverter.DoubleToInt64Bits(values[i]);
                BinaryPrimitives.WriteInt64LittleEndian(valueDestination.Slice(i * 8, 8), bits);
            }
        writer.Advance(checked(values.Length * sizeof(double)));

        SetOptionalLayout(ref state, writer.WrittenCount, 0, definitionByteCount);
    }

    static void EncodeOptionalDateTime(ReadOnlySpan<DateTime?> values, DateTimeKindHandling dateTimeKindHandling,
        ref ParquetWriter.RowGroupState.ColumnState state, string columnName, int maxEncodedBytes)
    {
        ValidateDateTimeHandling(dateTimeKindHandling, columnName);
        EncodeOptionalNullable<DateTime, OptionalDateTimeNullableWriter>(values, ref state, columnName, maxEncodedBytes,
            dateTimeKindHandling);
    }

    static void EncodeOptionalDateTimeAllDefined(ReadOnlySpan<DateTime> values, DateTimeKindHandling dateTimeKindHandling,
        ref ParquetWriter.RowGroupState.ColumnState state, string columnName, int maxEncodedBytes)
    {
        ValidateDateTimeHandling(dateTimeKindHandling, columnName);

        var writer = CreateBufferWriter(ref state, maxEncodedBytes, columnName);
        var definitionByteCount = WriteAllDefinedLevels(values.Length, writer);
        foreach (var value in values)
        {
            var unixMicros = ToUnixMicroseconds(value, dateTimeKindHandling, columnName);
            WriteInt64(writer, unixMicros);
        }

        SetOptionalLayout(ref state, writer.WrittenCount, 0, definitionByteCount);
    }

    static void EncodeOptionalString(ReadOnlySpan<string?> values, ref ParquetWriter.RowGroupState.ColumnState state,
        string columnName, int maxEncodedBytes)
    {
        var writer = CreateBufferWriter(ref state, maxEncodedBytes, columnName);
        var sizePassStarted = Stopwatch.GetTimestamp();
        var nonNullCount = 0;
        foreach (var value in values)
        {
            if (value is null)
                continue;

            nonNullCount++;
            _ = Utf8.GetByteCount(value);
        }
        var sizePassCompleted = Stopwatch.GetTimestamp();

        var definitionStarted = Stopwatch.GetTimestamp();
        var definitionByteCount = WriteDefinitionLevels(values, writer);
        var definitionCompleted = Stopwatch.GetTimestamp();

        long utf8WritePassTicks = 0;
        foreach (var value in values)
        {
            if (value is null)
                continue;

            var payloadLength = Utf8.GetByteCount(value);
            WriteInt32(writer, payloadLength);
            if (payloadLength == 0)
                continue;

            var writeStarted = Stopwatch.GetTimestamp();
            var destination = writer.GetSpan(payloadLength);
            var written = Utf8.GetBytes(value.AsSpan(), destination);
            var writeCompleted = Stopwatch.GetTimestamp();
            utf8WritePassTicks += writeCompleted - writeStarted;
            if (written != payloadLength)
                throw new InvalidOperationException($"Column '{columnName}' could not encode UTF-8 payload.");
            writer.Advance(written);
        }

        SetOptionalLayout(ref state, writer.WrittenCount, values.Length - nonNullCount, definitionByteCount);
        state.StringRowCount = values.Length;
        state.StringNonNullCount = nonNullCount;
        state.StringSizePassTicks = sizePassCompleted - sizePassStarted;
        state.StringDefinitionLevelsTicks = definitionCompleted - definitionStarted;
        state.StringByteCountPassTicks = 0; // Currently merged into size pass.
        state.StringUtf8WritePassTicks = utf8WritePassTicks;
    }

    static void EncodeOptionalByteArray(ReadOnlySpan<byte[]?> values, ref ParquetWriter.RowGroupState.ColumnState state,
        string columnName, int maxEncodedBytes)
    {
        var writer = CreateBufferWriter(ref state, maxEncodedBytes, columnName);
        var nonNullCount = 0;
        foreach (var value in values)
        {
            if (value is null)
                continue;

            nonNullCount++;
        }

        var definitionByteCount = WriteDefinitionLevels(values, writer);

        foreach (var value in values)
        {
            if (value is null)
                continue;

            WriteInt32(writer, value.Length);
            if (value.Length == 0)
                continue;

            var destination = writer.GetSpan(value.Length);
            value.AsSpan().CopyTo(destination);
            writer.Advance(value.Length);
        }

        SetOptionalLayout(ref state, writer.WrittenCount, values.Length - nonNullCount, definitionByteCount);
    }

    static void SetOptionalLayout(ref ParquetWriter.RowGroupState.ColumnState state, int totalByteCount, int nullCount,
        int definitionByteCount)
    {
        state.EncodedLength = totalByteCount;
        state.UncompressedLength = totalByteCount;
        state.NullCount = nullCount;
        state.DefinitionLevelsByteLength = definitionByteCount;
        state.RepetitionLevelsByteLength = 0;
    }
}
