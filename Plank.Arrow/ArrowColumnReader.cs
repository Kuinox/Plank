using Apache.Arrow;
using Apache.Arrow.Arrays;
using Apache.Arrow.Memory;
using Apache.Arrow.Types;
using Plank.Reading.Logical;
using Plank.Schema;

namespace Plank.Arrow;

static class ArrowColumnReader
{
    public static RecordBatch ReadRecordBatch(RowGroup rowGroup, Apache.Arrow.Schema schema,
        IReadOnlyList<LeafColumn> columns)
    {
        var rowCount = checked((int)rowGroup.RowCount);
        var arrays = new IArrowArray[columns.Count];
        var count = 0;
        try
        {
            for (; count < arrays.Length; count++)
                arrays[count] = Read(rowGroup, columns[count], schema.GetFieldByIndex(count), rowCount);
            return new RecordBatch(schema, arrays, rowCount);
        }
        catch
        {
            for (var i = 0; i < count; i++)
                arrays[i].Dispose();
            throw;
        }
    }

    public static IArrowArray CreateEmpty(Field field)
        => field.DataType switch
        {
            BooleanType => new BooleanArray.Builder().Build(),
            Int8Type => new Int8Array.Builder().Build(),
            Int16Type => new Int16Array.Builder().Build(),
            Int32Type => new Int32Array.Builder().Build(),
            Int64Type => new Int64Array.Builder().Build(),
            UInt8Type => new UInt8Array.Builder().Build(),
            UInt16Type => new UInt16Array.Builder().Build(),
            UInt32Type => new UInt32Array.Builder().Build(),
            UInt64Type => new UInt64Array.Builder().Build(),
            FloatType => new FloatArray.Builder().Build(),
            DoubleType => new DoubleArray.Builder().Build(),
            StringType => new StringArray.Builder().Build(),
            BinaryType => new BinaryArray.Builder().Build(),
            FixedSizeBinaryType fixedBinary => CreateEmptyFixedBinary(fixedBinary),
            GuidType => new GuidArray.Builder().Build(),
            Date32Type => new Date32Array(ArrowBuffer.Empty, ArrowBuffer.Empty, 0, 0, 0),
            Time32Type time32 => new Time32Array(time32, ArrowBuffer.Empty, ArrowBuffer.Empty, 0, 0, 0),
            Time64Type time64 => new Time64Array(time64, ArrowBuffer.Empty, ArrowBuffer.Empty, 0, 0, 0),
            TimestampType timestamp => new TimestampArray(timestamp, ArrowBuffer.Empty, ArrowBuffer.Empty, 0, 0, 0),
            _ => throw new NotSupportedException(
                $"Arrow adapter does not support empty arrays of type '{field.DataType.Name}'.")
        };

    static IArrowArray Read(RowGroup rowGroup, LeafColumn column, Field field, int rowCount)
        => field.DataType switch
        {
            BooleanType => ReadBoolean(rowGroup, column, field.IsNullable, rowCount),
            Int8Type => ReadInt8(rowGroup, column, field.IsNullable, rowCount),
            Int16Type => ReadInt16(rowGroup, column, field.IsNullable, rowCount),
            Int32Type => ReadPrimitive<int, Int32Array, Int32Array.Builder>(rowGroup, column, field.IsNullable,
                rowCount, new Int32Array.Builder()),
            Int64Type => ReadPrimitive<long, Int64Array, Int64Array.Builder>(rowGroup, column, field.IsNullable,
                rowCount, new Int64Array.Builder()),
            UInt8Type => ReadPrimitive<byte, UInt8Array, UInt8Array.Builder>(rowGroup, column, field.IsNullable,
                rowCount, new UInt8Array.Builder()),
            UInt16Type => ReadPrimitive<ushort, UInt16Array, UInt16Array.Builder>(rowGroup, column, field.IsNullable,
                rowCount, new UInt16Array.Builder()),
            UInt32Type => ReadPrimitive<uint, UInt32Array, UInt32Array.Builder>(rowGroup, column, field.IsNullable,
                rowCount, new UInt32Array.Builder()),
            UInt64Type => ReadPrimitive<ulong, UInt64Array, UInt64Array.Builder>(rowGroup, column, field.IsNullable,
                rowCount, new UInt64Array.Builder()),
            FloatType => ReadPrimitive<float, FloatArray, FloatArray.Builder>(rowGroup, column, field.IsNullable,
                rowCount, new FloatArray.Builder()),
            DoubleType => ReadPrimitive<double, DoubleArray, DoubleArray.Builder>(rowGroup, column, field.IsNullable,
                rowCount, new DoubleArray.Builder()),
            StringType => ReadVariable<StringArray, StringArray.Builder>(rowGroup, column, rowCount,
                new StringArray.Builder()),
            BinaryType => ReadVariable<BinaryArray, BinaryArray.Builder>(rowGroup, column, rowCount,
                new BinaryArray.Builder()),
            FixedSizeBinaryType fixedBinary => ReadFixedBinary(rowGroup, column, fixedBinary, rowCount),
            GuidType => ReadGuid(rowGroup, column, field.IsNullable, rowCount),
            Date32Type => ReadRaw<int>(rowGroup, column, field.IsNullable, rowCount,
                static (values, validity, length, nullCount) =>
                    new Date32Array(values, validity, length, nullCount, 0)),
            Time32Type time32 => ReadRaw<int>(rowGroup, column, field.IsNullable, rowCount,
                (values, validity, length, nullCount) =>
                    new Time32Array(time32, values, validity, length, nullCount, 0)),
            Time64Type time64 => ReadRaw<long>(rowGroup, column, field.IsNullable, rowCount,
                (values, validity, length, nullCount) =>
                    new Time64Array(time64, values, validity, length, nullCount, 0)),
            TimestampType timestamp => ReadRaw<long>(rowGroup, column, field.IsNullable, rowCount,
                (values, validity, length, nullCount) =>
                    new TimestampArray(timestamp, values, validity, length, nullCount, 0)),
            _ => throw new NotSupportedException(
                $"Arrow adapter does not support reading type '{field.DataType.Name}' for field '{field.Name}'.")
        };

    static BooleanArray ReadBoolean(RowGroup rowGroup, LeafColumn column, bool nullable, int rowCount)
    {
        var builder = new BooleanArray.Builder().Reserve(rowCount);
        if (!nullable)
        {
            foreach (var buffer in rowGroup.Column<bool>(column))
                builder.Append(buffer.Values);
            return builder.Build();
        }

        foreach (var buffer in rowGroup.Column<bool?>(column))
            foreach (var value in buffer.Values)
                if (value is { } present)
                    builder.Append(present);
                else
                    builder.AppendNull();
        return builder.Build();
    }

    static Int8Array ReadInt8(RowGroup rowGroup, LeafColumn column, bool nullable, int rowCount)
    {
        var builder = new Int8Array.Builder().Reserve(rowCount);
        if (!nullable)
        {
            foreach (var buffer in rowGroup.Column<int>(column))
                foreach (var value in buffer.Values)
                    builder.Append(checked((sbyte)value));
            return builder.Build();
        }

        foreach (var buffer in rowGroup.Column<int?>(column))
            foreach (var value in buffer.Values)
                if (value is { } present)
                    builder.Append(checked((sbyte)present));
                else
                    builder.AppendNull();
        return builder.Build();
    }

    static Int16Array ReadInt16(RowGroup rowGroup, LeafColumn column, bool nullable, int rowCount)
    {
        var builder = new Int16Array.Builder().Reserve(rowCount);
        if (!nullable)
        {
            foreach (var buffer in rowGroup.Column<int>(column))
                foreach (var value in buffer.Values)
                    builder.Append(checked((short)value));
            return builder.Build();
        }

        foreach (var buffer in rowGroup.Column<int?>(column))
            foreach (var value in buffer.Values)
                if (value is { } present)
                    builder.Append(checked((short)present));
                else
                    builder.AppendNull();
        return builder.Build();
    }

    static TArray ReadPrimitive<T, TArray, TBuilder>(RowGroup rowGroup, LeafColumn column, bool nullable,
        int rowCount, TBuilder builder)
        where T : struct, IEquatable<T>
        where TArray : IArrowArray
        where TBuilder : class, IArrowArrayBuilder<T, TArray, TBuilder>
    {
        builder.Reserve(rowCount);
        if (!nullable)
        {
            foreach (var buffer in rowGroup.Column<T>(column))
                builder.Append(buffer.Values);
            return builder.Build(default);
        }

        foreach (var buffer in rowGroup.Column<T?>(column))
            foreach (var value in buffer.Values)
                if (value is { } present)
                    builder.Append(present);
                else
                    builder.AppendNull();
        return builder.Build(default);
    }

    static TArray ReadVariable<TArray, TBuilder>(RowGroup rowGroup, LeafColumn column, int rowCount,
        TBuilder builder)
        where TArray : IArrowArray
        where TBuilder : class, IArrowArrayBuilder<byte, TArray, TBuilder>
    {
        builder.Reserve(rowCount);
        foreach (var buffer in rowGroup.Column<byte>(column))
            for (var i = 0; i < buffer.Count; i++)
                if (buffer.IsNull(i))
                    builder.AppendNull();
                else
                    builder.Append(buffer.GetValue(i));
        return builder.Build(default);
    }

    static FixedSizeBinaryArray ReadFixedBinary(RowGroup rowGroup, LeafColumn column, FixedSizeBinaryType type,
        int rowCount)
    {
        var values = new ArrowBuffer.Builder<byte>(checked(rowCount * type.ByteWidth));
        var validity = new ArrowBuffer.BitmapBuilder(rowCount);
        var nullValue = new byte[type.ByteWidth];
        foreach (var buffer in rowGroup.Column<byte>(column))
            for (var i = 0; i < buffer.Count; i++)
            {
                if (buffer.IsNull(i))
                {
                    values.Append(nullValue);
                    validity.Append(false);
                    continue;
                }

                var value = buffer.GetValue(i);
                if (value.Length != type.ByteWidth)
                    throw new InvalidDataException(
                        $"Column '{column.Path}' contains {value.Length} bytes; expected {type.ByteWidth}.");
                values.Append(value);
                validity.Append(true);
            }

        return new FixedSizeBinaryArray(new ArrayData(type, rowCount, validity.UnsetBitCount, 0,
            [BuildValidity(validity), values.Build()]));
    }

    static GuidArray ReadGuid(RowGroup rowGroup, LeafColumn column, bool nullable, int rowCount)
    {
        var builder = new GuidArray.Builder().Reserve(rowCount);
        foreach (var buffer in rowGroup.Column<byte>(column))
            for (var i = 0; i < buffer.Count; i++)
                if (buffer.IsNull(i))
                {
                    if (!nullable)
                        throw new InvalidDataException($"Required UUID column '{column.Path}' contains a null.");
                    builder.AppendNull();
                }
                else
                {
                    var value = buffer.GetValue(i);
                    if (value.Length != 16)
                        throw new InvalidDataException(
                            $"UUID column '{column.Path}' contains {value.Length} bytes; expected 16.");
                    builder.Append(value);
                }
        return builder.Build();
    }

    static IArrowArray ReadRaw<T>(RowGroup rowGroup, LeafColumn column, bool nullable, int rowCount,
        RawArrayFactory<T> create)
        where T : struct
    {
        var values = new ArrowBuffer.Builder<T>(rowCount);
        var validity = new ArrowBuffer.BitmapBuilder(rowCount);
        if (!nullable)
        {
            foreach (var buffer in rowGroup.Column<T>(column))
            {
                values.Append(buffer.Values);
                validity.AppendRange(true, buffer.Count);
            }
        }
        else
        {
            foreach (var buffer in rowGroup.Column<T?>(column))
                foreach (var value in buffer.Values)
                {
                    values.Append(value.GetValueOrDefault());
                    validity.Append(value.HasValue);
                }
        }

        return create(values.Build(), BuildValidity(validity), rowCount, validity.UnsetBitCount);
    }

    static FixedSizeBinaryArray CreateEmptyFixedBinary(FixedSizeBinaryType type)
        => new(new ArrayData(type, 0, 0, 0, [ArrowBuffer.Empty, ArrowBuffer.Empty]));

    static ArrowBuffer BuildValidity(ArrowBuffer.BitmapBuilder validity)
        => validity.UnsetBitCount == 0 ? ArrowBuffer.Empty : validity.Build();

    delegate IArrowArray RawArrayFactory<T>(ArrowBuffer values, ArrowBuffer validity, int length, int nullCount)
        where T : struct;
}
