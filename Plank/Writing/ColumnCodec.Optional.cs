using Plank.Schema;
using System.Runtime.CompilerServices;

namespace Plank.Writing;

static partial class ColumnCodec
{
    interface IOptionalScalarWriter<T>
    {
        static abstract int ValueSize { get; }
        static abstract void Write(T value, ColumnBufferWriter writer, DateTimeKindHandling dateTimeKindHandling,
            string columnName);
    }

    interface IOptionalReferenceWriter<T> where T : class
    {
        static abstract void WritePayload(T value, ColumnBufferWriter writer, string columnName);
    }

    readonly struct OptionalInt32Writer : IOptionalScalarWriter<int>
    {
        public static int ValueSize => sizeof(int);

        public static void Write(int value, ColumnBufferWriter writer, DateTimeKindHandling dateTimeKindHandling,
            string columnName)
            => WriteInt32(writer, value);
    }

    readonly struct OptionalInt64Writer : IOptionalScalarWriter<long>
    {
        public static int ValueSize => sizeof(long);

        public static void Write(long value, ColumnBufferWriter writer, DateTimeKindHandling dateTimeKindHandling,
            string columnName)
            => WriteInt64(writer, value);
    }

    readonly struct OptionalDoubleWriter : IOptionalScalarWriter<double>
    {
        public static int ValueSize => sizeof(double);

        public static void Write(double value, ColumnBufferWriter writer, DateTimeKindHandling dateTimeKindHandling,
            string columnName)
            => WriteInt64(writer, BitConverter.DoubleToInt64Bits(value));
    }

    readonly struct OptionalDateTimeWriter : IOptionalScalarWriter<DateTime>
    {
        public static int ValueSize => sizeof(long);

        public static void Write(DateTime value, ColumnBufferWriter writer, DateTimeKindHandling dateTimeKindHandling,
            string columnName)
            => WriteInt64(writer, ToUnixMicroseconds(value, dateTimeKindHandling, columnName));
    }

    readonly struct OptionalStringWriter : IOptionalReferenceWriter<string>
    {
        public static void WritePayload(string value, ColumnBufferWriter writer, string columnName)
            => WriteStringPayload(writer, value, columnName);
    }

    readonly struct OptionalByteArrayWriter : IOptionalReferenceWriter<byte[]>
    {
        public static void WritePayload(byte[] value, ColumnBufferWriter writer, string columnName)
            => WriteByteArrayPayload(writer, value);
    }

    static bool TryEncodeOptional<T>(Column column, ReadOnlySpan<T> values, ColumnDispatch.DispatchKey dispatchKey,
        int encodedBufferCapacity, DateTimeKindHandling dateTimeKindHandling,
        ref ParquetWriter.RowGroupState.ColumnState state)
    {
        switch (dispatchKey)
        {
            case ColumnDispatch.DispatchKey.Int32Int32:
                EncodeOptionalAllDefined<int, OptionalInt32Writer>(Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<int>>(ref values),
                    ref state, column.Name, encodedBufferCapacity, DateTimeKindHandling.None);
                return true;
            case ColumnDispatch.DispatchKey.Int32NullableInt32:
                EncodeOptionalNullable<int, OptionalInt32Writer>(Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<int?>>(ref values),
                    ref state, column.Name, encodedBufferCapacity, DateTimeKindHandling.None);
                return true;
            case ColumnDispatch.DispatchKey.Int64Int64:
                EncodeOptionalAllDefined<long, OptionalInt64Writer>(Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<long>>(ref values),
                    ref state, column.Name, encodedBufferCapacity, DateTimeKindHandling.None);
                return true;
            case ColumnDispatch.DispatchKey.Int64NullableInt64:
                EncodeOptionalNullable<long, OptionalInt64Writer>(Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<long?>>(ref values),
                    ref state, column.Name, encodedBufferCapacity, DateTimeKindHandling.None);
                return true;
            case ColumnDispatch.DispatchKey.Int64DateTime:
                EncodeOptionalAllDefined<DateTime, OptionalDateTimeWriter>(Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<DateTime>>(ref values),
                    ref state, column.Name, encodedBufferCapacity, dateTimeKindHandling);
                return true;
            case ColumnDispatch.DispatchKey.Int64NullableDateTime:
                EncodeOptionalNullable<DateTime, OptionalDateTimeWriter>(Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<DateTime?>>(ref values),
                    ref state, column.Name, encodedBufferCapacity, dateTimeKindHandling);
                return true;
            case ColumnDispatch.DispatchKey.ByteArrayString:
                EncodeOptionalReference<string, OptionalStringWriter>(Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<string?>>(ref values),
                    ref state, column.Name, encodedBufferCapacity);
                return true;
            case ColumnDispatch.DispatchKey.ByteArrayByteArray:
                EncodeOptionalReference<byte[], OptionalByteArrayWriter>(Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<byte[]?>>(ref values),
                    ref state, column.Name, encodedBufferCapacity);
                return true;
            case ColumnDispatch.DispatchKey.DoubleDouble:
                EncodeOptionalAllDefined<double, OptionalDoubleWriter>(Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<double>>(ref values),
                    ref state, column.Name, encodedBufferCapacity, DateTimeKindHandling.None);
                return true;
            case ColumnDispatch.DispatchKey.DoubleNullableDouble:
                EncodeOptionalNullable<double, OptionalDoubleWriter>(Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<double?>>(ref values),
                    ref state, column.Name, encodedBufferCapacity, DateTimeKindHandling.None);
                return true;
        }
        return false;
    }

    static void EncodeOptionalNullable<T, TWriter>(ReadOnlySpan<T?> values,
        ref ParquetWriter.RowGroupState.ColumnState state, string columnName, int maxEncodedBytes,
        DateTimeKindHandling dateTimeKindHandling)
        where T : struct
        where TWriter : struct, IOptionalScalarWriter<T>
    {
        if (typeof(T) == typeof(DateTime))
            ValidateDateTimeHandling(dateTimeKindHandling, columnName);

        var writer = CreateBufferWriter(ref state, maxEncodedBytes, columnName);
        var nonNullCount = 0;
        foreach (var t in values)
            if (t.HasValue)
                nonNullCount++;

        var definitionByteCount = WriteDefinitionLevels(values, writer);
        foreach (var value in values)
        {
            if (!value.HasValue)
                continue;

            TWriter.Write(value.Value, writer, dateTimeKindHandling, columnName);
        }

        SetOptionalLayout(ref state, writer.WrittenCount, values.Length - nonNullCount, definitionByteCount);
    }

    static void EncodeOptionalAllDefined<T, TWriter>(ReadOnlySpan<T> values,
        ref ParquetWriter.RowGroupState.ColumnState state, string columnName, int maxEncodedBytes,
        DateTimeKindHandling dateTimeKindHandling)
        where TWriter : struct, IOptionalScalarWriter<T>
    {
        if (typeof(T) == typeof(DateTime))
            ValidateDateTimeHandling(dateTimeKindHandling, columnName);

        var writer = CreateBufferWriter(ref state, maxEncodedBytes, columnName);
        var definitionByteCount = WriteAllDefinedLevels(values.Length, writer);
        foreach (var value in values)
            TWriter.Write(value, writer, dateTimeKindHandling, columnName);

        SetOptionalLayout(ref state, writer.WrittenCount, 0, definitionByteCount);
    }

    static void EncodeOptionalReference<T, TWriter>(ReadOnlySpan<T?> values,
        ref ParquetWriter.RowGroupState.ColumnState state, string columnName, int maxEncodedBytes)
        where T : class
        where TWriter : struct, IOptionalReferenceWriter<T>
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

            TWriter.WritePayload(value, writer, columnName);
        }

        SetOptionalLayout(ref state, writer.WrittenCount, values.Length - nonNullCount, definitionByteCount);
        state.StringRowCount = values.Length;
        state.StringNonNullCount = nonNullCount;
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
