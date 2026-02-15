using Plank.Schema;
using System.Runtime.CompilerServices;

namespace Plank.Writing;

static partial class Encoding
{
    static bool TryEncodeOptional<T>(Column column, ReadOnlySpan<T> values, ParquetPhysicalType physicalType,
        VariableSizeBuffer writer, DateTimeKindHandling dateTimeKindHandling,
        ref ParquetWriter.RowGroupState.ColumnState state)
    {
        var dispatchKey = ColumnDispatch.GetDispatchKey(physicalType, ColumnDispatch.GetValueKind<T>());
        switch (dispatchKey)
        {
            case ColumnDispatch.DispatchKey.Int32Int32:
                EncodeOptionalInt32AllDefined(Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<int>>(ref values), writer,
                    ref state);
                return true;
            case ColumnDispatch.DispatchKey.Int32NullableInt32:
                EncodeOptionalNullableInt32(Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<int?>>(ref values), writer,
                    ref state);
                return true;
            case ColumnDispatch.DispatchKey.Int64Int64:
                EncodeOptionalInt64AllDefined(Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<long>>(ref values), writer,
                    ref state);
                return true;
            case ColumnDispatch.DispatchKey.Int64NullableInt64:
                EncodeOptionalNullableInt64(Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<long?>>(ref values), writer,
                    ref state);
                return true;
            case ColumnDispatch.DispatchKey.Int64DateTime:
                EncodeOptionalDateTimeAllDefined(Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<DateTime>>(ref values), writer,
                    ref state, dateTimeKindHandling, column.Name);
                return true;
            case ColumnDispatch.DispatchKey.Int64NullableDateTime:
                EncodeOptionalNullableDateTime(Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<DateTime?>>(ref values), writer,
                    ref state, dateTimeKindHandling, column.Name);
                return true;
            case ColumnDispatch.DispatchKey.ByteArrayString:
                EncodeOptionalString(Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<string?>>(ref values), writer,
                    ref state, column.Name);
                return true;
            case ColumnDispatch.DispatchKey.ByteArrayByteArray:
                EncodeOptionalByteArray(Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<byte[]?>>(ref values), writer,
                    ref state, column.Name);
                return true;
            case ColumnDispatch.DispatchKey.DoubleDouble:
                EncodeOptionalDoubleAllDefined(Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<double>>(ref values), writer,
                    ref state);
                return true;
            case ColumnDispatch.DispatchKey.DoubleNullableDouble:
                EncodeOptionalNullableDouble(Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<double?>>(ref values), writer,
                    ref state);
                return true;
            default:
                return false;
        }
    }

    static void EncodeOptionalInt32AllDefined(ReadOnlySpan<int> values, VariableSizeBuffer writer,
        ref ParquetWriter.RowGroupState.ColumnState state)
    {
        var definitionByteCount = WriteAllDefinedLevels(values.Length, writer);
        foreach (var value in values)
            WriteInt32(writer, value);

        SetOptionalLayout(ref state, writer._written, 0, definitionByteCount);
    }

    static void EncodeOptionalNullableInt32(ReadOnlySpan<int?> values, VariableSizeBuffer writer,
        ref ParquetWriter.RowGroupState.ColumnState state)
    {
        var nonNullCount = 0;
        foreach (var value in values)
            if (value.HasValue)
                nonNullCount++;

        var definitionByteCount = WriteDefinitionLevels(values, writer);
        foreach (var value in values)
            if (value.HasValue)
                WriteInt32(writer, value.Value);

        SetOptionalLayout(ref state, writer._written, values.Length - nonNullCount, definitionByteCount);
    }

    static void EncodeOptionalInt64AllDefined(ReadOnlySpan<long> values, VariableSizeBuffer writer,
        ref ParquetWriter.RowGroupState.ColumnState state)
    {
        var definitionByteCount = WriteAllDefinedLevels(values.Length, writer);
        foreach (var value in values)
            WriteInt64(writer, value);

        SetOptionalLayout(ref state, writer._written, 0, definitionByteCount);
    }

    static void EncodeOptionalNullableInt64(ReadOnlySpan<long?> values, VariableSizeBuffer writer,
        ref ParquetWriter.RowGroupState.ColumnState state)
    {
        var nonNullCount = 0;
        foreach (var value in values)
            if (value.HasValue)
                nonNullCount++;

        var definitionByteCount = WriteDefinitionLevels(values, writer);
        foreach (var value in values)
            if (value.HasValue)
                WriteInt64(writer, value.Value);

        SetOptionalLayout(ref state, writer._written, values.Length - nonNullCount, definitionByteCount);
    }

    static void EncodeOptionalDoubleAllDefined(ReadOnlySpan<double> values, VariableSizeBuffer writer,
        ref ParquetWriter.RowGroupState.ColumnState state)
    {
        var definitionByteCount = WriteAllDefinedLevels(values.Length, writer);
        foreach (var value in values)
            WriteInt64(writer, BitConverter.DoubleToInt64Bits(value));

        SetOptionalLayout(ref state, writer._written, 0, definitionByteCount);
    }

    static void EncodeOptionalNullableDouble(ReadOnlySpan<double?> values, VariableSizeBuffer writer,
        ref ParquetWriter.RowGroupState.ColumnState state)
    {
        var nonNullCount = 0;
        foreach (var value in values)
            if (value.HasValue)
                nonNullCount++;

        var definitionByteCount = WriteDefinitionLevels(values, writer);
        foreach (var value in values)
            if (value.HasValue)
                WriteInt64(writer, BitConverter.DoubleToInt64Bits(value.Value));

        SetOptionalLayout(ref state, writer._written, values.Length - nonNullCount, definitionByteCount);
    }

    static void EncodeOptionalDateTimeAllDefined(ReadOnlySpan<DateTime> values, VariableSizeBuffer writer,
        ref ParquetWriter.RowGroupState.ColumnState state, DateTimeKindHandling dateTimeKindHandling,
        string columnName)
    {
        ValidateDateTimeHandling(dateTimeKindHandling, columnName);

        var definitionByteCount = WriteAllDefinedLevels(values.Length, writer);
        foreach (var value in values)
            WriteInt64(writer, ToUnixMicroseconds(value, dateTimeKindHandling, columnName));

        SetOptionalLayout(ref state, writer._written, 0, definitionByteCount);
    }

    static void EncodeOptionalNullableDateTime(ReadOnlySpan<DateTime?> values, VariableSizeBuffer writer,
        ref ParquetWriter.RowGroupState.ColumnState state, DateTimeKindHandling dateTimeKindHandling,
        string columnName)
    {
        ValidateDateTimeHandling(dateTimeKindHandling, columnName);

        var nonNullCount = 0;
        foreach (var value in values)
            if (value.HasValue)
                nonNullCount++;

        var definitionByteCount = WriteDefinitionLevels(values, writer);
        foreach (var value in values)
            if (value.HasValue)
                WriteInt64(writer, ToUnixMicroseconds(value.Value, dateTimeKindHandling, columnName));

        SetOptionalLayout(ref state, writer._written, values.Length - nonNullCount, definitionByteCount);
    }

    static void EncodeOptionalString(ReadOnlySpan<string?> values, VariableSizeBuffer writer,
        ref ParquetWriter.RowGroupState.ColumnState state, string columnName)
    {
        var nonNullCount = 0;
        foreach (var value in values)
            if (value is not null)
                nonNullCount++;

        var definitionByteCount = WriteDefinitionLevels(values, writer);
        foreach (var value in values)
            if (value is not null)
                WriteStringPayload(writer, value, columnName);

        SetOptionalLayout(ref state, writer._written, values.Length - nonNullCount, definitionByteCount);
        state.StringRowCount = values.Length;
        state.StringNonNullCount = nonNullCount;
    }

    static void EncodeOptionalByteArray(ReadOnlySpan<byte[]?> values, VariableSizeBuffer writer,
        ref ParquetWriter.RowGroupState.ColumnState state, string columnName)
    {
        var nonNullCount = 0;
        foreach (var value in values)
            if (value is not null)
                nonNullCount++;

        var definitionByteCount = WriteDefinitionLevels(values, writer);
        foreach (var value in values)
            if (value is not null)
                WriteByteArrayPayload(writer, value);

        SetOptionalLayout(ref state, writer._written, values.Length - nonNullCount, definitionByteCount);
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
