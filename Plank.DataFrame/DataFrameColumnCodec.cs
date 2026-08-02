using System.Text;
using Microsoft.Data.Analysis;
using Plank.Reading.Logical;
using Plank.Schema;
using Plank.Writing;

namespace Plank.DataFrame;

internal abstract class DataFrameColumnCodec
{
    public abstract DataFrameColumn Column { get; }

    public abstract ColumnDefinition Definition { get; }

    public abstract void BindWriter(ParquetWriter writer, LeafColumn leaf);

    public abstract void Write(RowGroupWriter rowGroup, long offset, int count);

    public abstract void Read(RowGroup rowGroup, LeafColumn leaf, long offset);

    public static DataFrameColumnCodec CreateForWriting(DataFrameColumn column)
    {
        ArgumentNullException.ThrowIfNull(column);
        var optional = column.NullCount > 0;
        var name = column.Name;
        var type = column.DataType;

        if (type == typeof(bool))
            return Primitive<bool, bool>(column, optional, ParquetPhysicalType.Boolean, logicalType: null,
                static value => value, static value => value);
        if (type == typeof(byte))
            return Primitive<byte, byte>(column, optional, ParquetPhysicalType.Int32, new LogicalType.Int(8, isSigned: false),
                static value => value, static value => value);
        if (type == typeof(sbyte))
            return Primitive<sbyte, int>(column, optional, ParquetPhysicalType.Int32,
                new LogicalType.Int(8, isSigned: true), static value => value, static value => checked((sbyte)value));
        if (type == typeof(short))
            return Primitive<short, int>(column, optional, ParquetPhysicalType.Int32,
                new LogicalType.Int(16, isSigned: true), static value => value, static value => checked((short)value));
        if (type == typeof(ushort))
            return Primitive<ushort, ushort>(column, optional, ParquetPhysicalType.Int32, new LogicalType.Int(16, isSigned: false),
                static value => value, static value => value);
        if (type == typeof(int))
            return Primitive<int, int>(column, optional, ParquetPhysicalType.Int32, logicalType: null,
                static value => value, static value => value);
        if (type == typeof(uint))
            return Primitive<uint, uint>(column, optional, ParquetPhysicalType.Int32, new LogicalType.Int(32, isSigned: false),
                static value => value, static value => value);
        if (type == typeof(long))
            return Primitive<long, long>(column, optional, ParquetPhysicalType.Int64, logicalType: null,
                static value => value, static value => value);
        if (type == typeof(ulong))
            return Primitive<ulong, ulong>(column, optional, ParquetPhysicalType.Int64, new LogicalType.Int(64, isSigned: false),
                static value => value, static value => value);
        if (type == typeof(float))
            return Primitive<float, float>(column, optional, ParquetPhysicalType.Float, logicalType: null,
                static value => value, static value => value);
        if (type == typeof(double))
            return Primitive<double, double>(column, optional, ParquetPhysicalType.Double, logicalType: null,
                static value => value, static value => value);
        if (type == typeof(string))
            return new StringColumnCodec(column, optional, new LogicalType.String());
        if (type == typeof(byte[]))
            return new BinaryColumnCodec(column, optional, ParquetPhysicalType.ByteArray, logicalType: null);
        if (type == typeof(Guid))
            return new GuidColumnCodec(column, optional);
        if (type == typeof(DateOnly))
            return Primitive<DateOnly, DateOnly>(column, optional, ParquetPhysicalType.Int32, new LogicalType.Date(),
                static value => value, static value => value);
        if (type == typeof(TimeOnly))
            return Primitive<TimeOnly, TimeOnly>(column, optional, ParquetPhysicalType.Int64,
                new LogicalType.Time(TimeUnit.Micros, IsAdjustedToUtc: false),
                static value => value, static value => value);
        if (type == typeof(DateTime))
            return Primitive<DateTime, DateTime>(column, optional, ParquetPhysicalType.Int64,
                new LogicalType.Timestamp(TimeUnit.Micros, IsAdjustedToUtc: false),
                static value => value, static value => value);
        if (type == typeof(DateTimeOffset))
            return Primitive<DateTimeOffset, DateTimeOffset>(column, optional, ParquetPhysicalType.Int64,
                new LogicalType.Timestamp(TimeUnit.Micros, IsAdjustedToUtc: true),
                static value => value, static value => value);

        throw new NotSupportedException(
            $"DataFrame column '{name}' has unsupported data type '{type}'. Plank.DataFrame supports bool, integer, floating-point, string, byte[], Guid, DateOnly, TimeOnly, DateTime, and DateTimeOffset columns.");
    }

    public static DataFrameColumnCodec CreateForReading(LeafColumn leaf, long rowCount)
    {
        ArgumentNullException.ThrowIfNull(leaf);
        var optional = GetOptional(leaf);
        if (leaf is
            {
                PhysicalType: ParquetPhysicalType.FixedLenByteArray,
                LogicalType: LogicalType.Uuid,
                Options.TypeLength: not 16
            })
            throw new NotSupportedException(
                $"UUID column '{leaf.Path}' has fixed length {leaf.Options.TypeLength}; expected 16 bytes.");
        return (leaf.PhysicalType, leaf.LogicalType) switch
        {
            (ParquetPhysicalType.Boolean, null) => Primitive<bool, bool>(leaf.Path, rowCount, optional,
                leaf.PhysicalType, leaf.LogicalType, static value => value, static value => value),
            (ParquetPhysicalType.Int32, LogicalType.Date) => Primitive<DateOnly, DateOnly>(leaf.Path, rowCount,
                optional, leaf.PhysicalType, leaf.LogicalType, static value => value, static value => value),
            (ParquetPhysicalType.Int32, LogicalType.Time { Unit: TimeUnit.Millis }) =>
                Primitive<TimeOnly, TimeOnly>(leaf.Path, rowCount, optional, leaf.PhysicalType, leaf.LogicalType,
                    static value => value, static value => value),
            (ParquetPhysicalType.Int32, LogicalType.Int { BitWidth: 8, IsSigned: false }) =>
                Primitive<byte, byte>(leaf.Path, rowCount, optional, leaf.PhysicalType, leaf.LogicalType,
                    static value => value, static value => value),
            (ParquetPhysicalType.Int32, LogicalType.Int { BitWidth: 8, IsSigned: true }) =>
                Primitive<sbyte, int>(leaf.Path, rowCount, optional, leaf.PhysicalType, leaf.LogicalType,
                    static value => value, static value => checked((sbyte)value)),
            (ParquetPhysicalType.Int32, LogicalType.Int { BitWidth: 16, IsSigned: false }) =>
                Primitive<ushort, ushort>(leaf.Path, rowCount, optional, leaf.PhysicalType, leaf.LogicalType,
                    static value => value, static value => value),
            (ParquetPhysicalType.Int32, LogicalType.Int { BitWidth: 16, IsSigned: true }) =>
                Primitive<short, int>(leaf.Path, rowCount, optional, leaf.PhysicalType, leaf.LogicalType,
                    static value => value, static value => checked((short)value)),
            (ParquetPhysicalType.Int32, LogicalType.Int { BitWidth: 32, IsSigned: false }) =>
                Primitive<uint, uint>(leaf.Path, rowCount, optional, leaf.PhysicalType, leaf.LogicalType,
                    static value => value, static value => value),
            (ParquetPhysicalType.Int32, LogicalType.Int { BitWidth: 32, IsSigned: true }) or
                (ParquetPhysicalType.Int32, null) => Primitive<int, int>(leaf.Path, rowCount, optional,
                    leaf.PhysicalType, leaf.LogicalType, static value => value, static value => value),
            (ParquetPhysicalType.Int64, LogicalType.Int { BitWidth: 64, IsSigned: false }) =>
                Primitive<ulong, ulong>(leaf.Path, rowCount, optional, leaf.PhysicalType, leaf.LogicalType,
                    static value => value, static value => value),
            (ParquetPhysicalType.Int64, LogicalType.Int { BitWidth: 64, IsSigned: true }) or
                (ParquetPhysicalType.Int64, null) => Primitive<long, long>(leaf.Path, rowCount, optional,
                    leaf.PhysicalType, leaf.LogicalType, static value => value, static value => value),
            (ParquetPhysicalType.Int64, LogicalType.Time) => Primitive<TimeOnly, TimeOnly>(leaf.Path, rowCount,
                optional, leaf.PhysicalType, leaf.LogicalType, static value => value, static value => value),
            (ParquetPhysicalType.Int64, LogicalType.Timestamp { IsAdjustedToUtc: false }) =>
                Primitive<DateTime, DateTime>(leaf.Path, rowCount, optional, leaf.PhysicalType, leaf.LogicalType,
                    static value => value, static value => value),
            (ParquetPhysicalType.Int64, LogicalType.Timestamp { IsAdjustedToUtc: true }) =>
                Primitive<DateTimeOffset, DateTimeOffset>(leaf.Path, rowCount, optional, leaf.PhysicalType,
                    leaf.LogicalType, static value => value, static value => value),
            (ParquetPhysicalType.Float, null) => Primitive<float, float>(leaf.Path, rowCount, optional,
                leaf.PhysicalType, leaf.LogicalType, static value => value, static value => value),
            (ParquetPhysicalType.Double, null) => Primitive<double, double>(leaf.Path, rowCount, optional,
                leaf.PhysicalType, leaf.LogicalType, static value => value, static value => value),
            (ParquetPhysicalType.ByteArray, LogicalType.String or LogicalType.Json) =>
                new StringColumnCodec(leaf.Path, rowCount, optional, leaf.LogicalType),
            (ParquetPhysicalType.ByteArray, null) or
                (ParquetPhysicalType.FixedLenByteArray, null) or
                (ParquetPhysicalType.Int96, null) => new BinaryColumnCodec(leaf.Path, rowCount, optional,
                    leaf.PhysicalType, leaf.LogicalType),
            (ParquetPhysicalType.FixedLenByteArray, LogicalType.Uuid) =>
                new GuidColumnCodec(leaf.Path, rowCount, optional),
            _ => throw new NotSupportedException(
                $"Parquet column '{leaf.Path}' has unsupported DataFrame mapping '{leaf.PhysicalType}' / '{leaf.LogicalType?.ToString() ?? "none"}'.")
        };
    }

    static PrimitiveColumnCodec<TDataFrame, TParquet> Primitive<TDataFrame, TParquet>(DataFrameColumn column,
        bool optional, ParquetPhysicalType physicalType, LogicalType? logicalType,
        Func<TDataFrame, TParquet> toParquet, Func<TParquet, TDataFrame> fromParquet)
        where TDataFrame : unmanaged
        where TParquet : unmanaged
        => new(column, optional, physicalType, logicalType, toParquet, fromParquet);

    static PrimitiveColumnCodec<TDataFrame, TParquet> Primitive<TDataFrame, TParquet>(string name, long rowCount,
        bool optional, ParquetPhysicalType physicalType, LogicalType? logicalType,
        Func<TDataFrame, TParquet> toParquet, Func<TParquet, TDataFrame> fromParquet)
        where TDataFrame : unmanaged
        where TParquet : unmanaged
        => new(name, rowCount, optional, physicalType, logicalType, toParquet, fromParquet);

    static bool GetOptional(LeafColumn leaf)
    {
        if (leaf.Options.Repetition == ParquetRepetition.Repeated)
            throw new NotSupportedException($"Repeated Parquet column '{leaf.Path}' cannot be materialized as a DataFrame column.");
        return leaf.Options.Repetition == ParquetRepetition.Optional;
    }

    static ColumnDefinition CreateDefinition(string name, bool optional, ParquetPhysicalType physicalType,
        LogicalType? logicalType, uint typeLength = 0)
    {
        var options = typeLength == 0 ? null : new ColumnOptions(typeLength: typeLength);
        return optional
            ? ColumnDefinition.OptionalLeaf(name, physicalType, options, logicalType)
            : ColumnDefinition.RequiredLeaf(name, physicalType, options, logicalType);
    }

    sealed class PrimitiveColumnCodec<TDataFrame, TParquet> : DataFrameColumnCodec
        where TDataFrame : unmanaged
        where TParquet : unmanaged
    {
        readonly DataFrameColumn _column;
        readonly bool _optional;
        readonly Func<TDataFrame, TParquet> _toParquet;
        readonly Func<TParquet, TDataFrame> _fromParquet;
        SerializedColumn<TParquet>? _requiredWriter;
        SerializedColumn<TParquet?>? _optionalWriter;

        public PrimitiveColumnCodec(DataFrameColumn column, bool optional, ParquetPhysicalType physicalType,
            LogicalType? logicalType, Func<TDataFrame, TParquet> toParquet,
            Func<TParquet, TDataFrame> fromParquet)
        {
            _column = column;
            _optional = optional;
            _toParquet = toParquet;
            _fromParquet = fromParquet;
            Definition = CreateDefinition(column.Name, optional, physicalType, logicalType);
        }

        public PrimitiveColumnCodec(string name, long rowCount, bool optional, ParquetPhysicalType physicalType,
            LogicalType? logicalType, Func<TDataFrame, TParquet> toParquet,
            Func<TParquet, TDataFrame> fromParquet)
        {
            _column = new PrimitiveDataFrameColumn<TDataFrame>(name, rowCount);
            _optional = optional;
            _toParquet = toParquet;
            _fromParquet = fromParquet;
            Definition = CreateDefinition(name, optional, physicalType, logicalType);
        }

        public override DataFrameColumn Column
            => _column;

        public override ColumnDefinition Definition { get; }

        public override void BindWriter(ParquetWriter writer, LeafColumn leaf)
        {
            if (_optional)
                _optionalWriter = writer.CreateSerializedColumn<TParquet?>(leaf);
            else
                _requiredWriter = writer.CreateSerializedColumn<TParquet>(leaf);
        }

        public override void Write(RowGroupWriter rowGroup, long offset, int count)
        {
            if (_optional)
            {
                var values = new TParquet?[count];
                for (var i = 0; i < values.Length; i++)
                    if (_column[offset + i] is TDataFrame value)
                        values[i] = _toParquet(value);
                var writer = _optionalWriter ?? throw new InvalidOperationException("The column writer is not bound.");
                writer.Serialize(values);
                rowGroup.Write(writer);
                return;
            }

            var required = new TParquet[count];
            for (var i = 0; i < required.Length; i++)
                required[i] = _column[offset + i] is TDataFrame value
                    ? _toParquet(value)
                    : throw new InvalidOperationException(
                        $"Required DataFrame column '{_column.Name}' contains null at row {offset + i}.");
            var requiredWriter = _requiredWriter ?? throw new InvalidOperationException("The column writer is not bound.");
            requiredWriter.Serialize(required);
            rowGroup.Write(requiredWriter);
        }

        public override void Read(RowGroup rowGroup, LeafColumn leaf, long offset)
        {
            var destination = (PrimitiveDataFrameColumn<TDataFrame>)_column;
            var count = 0L;
            if (_optional)
            {
                foreach (var buffer in rowGroup.Column<TParquet?>(leaf))
                    for (var i = 0; i < buffer.Count; i++)
                    {
                        var value = buffer.Values[i];
                        destination[offset + count++] = value is { } present ? _fromParquet(present) : null;
                    }
            }
            else
            {
                foreach (var buffer in rowGroup.Column<TParquet>(leaf))
                    for (var i = 0; i < buffer.Count; i++)
                        destination[offset + count++] = _fromParquet(buffer.Values[i]);
            }

            ValidateReadCount(rowGroup, leaf, count);
        }
    }

    sealed class StringColumnCodec : DataFrameColumnCodec
    {
        readonly DataFrameColumn _column;
        readonly bool _optional;
        SerializedColumn<string>? _writer;

        public StringColumnCodec(DataFrameColumn column, bool optional, LogicalType logicalType)
        {
            _column = column;
            _optional = optional;
            Definition = CreateDefinition(column.Name, optional, ParquetPhysicalType.ByteArray, logicalType);
        }

        public StringColumnCodec(string name, long rowCount, bool optional, LogicalType? logicalType)
        {
            _column = new StringDataFrameColumn(name, rowCount);
            _optional = optional;
            Definition = CreateDefinition(name, optional, ParquetPhysicalType.ByteArray, logicalType);
        }

        public override DataFrameColumn Column
            => _column;

        public override ColumnDefinition Definition { get; }

        public override void BindWriter(ParquetWriter writer, LeafColumn leaf)
            => _writer = writer.CreateSerializedColumn<string>(leaf);

        public override void Write(RowGroupWriter rowGroup, long offset, int count)
        {
            var values = new string[count];
            for (var i = 0; i < values.Length; i++)
            {
                var value = _column[offset + i];
                if (value is null && !_optional)
                    throw new InvalidOperationException(
                        $"Required DataFrame column '{_column.Name}' contains null at row {offset + i}.");
                values[i] = (string?)value!;
            }

            var writer = _writer ?? throw new InvalidOperationException("The column writer is not bound.");
            writer.Serialize(values);
            rowGroup.Write(writer);
        }

        public override void Read(RowGroup rowGroup, LeafColumn leaf, long offset)
        {
            var destination = (StringDataFrameColumn)_column;
            var count = 0L;
            foreach (var buffer in rowGroup.Column<byte>(leaf))
                for (var i = 0; i < buffer.Count; i++)
                    destination[offset + count++] = buffer.IsNull(i)
                        ? null
                        : Encoding.UTF8.GetString(buffer.GetValue(i));
            ValidateReadCount(rowGroup, leaf, count);
        }
    }

    sealed class BinaryColumnCodec : DataFrameColumnCodec
    {
        readonly DataFrameColumn _column;
        readonly bool _optional;
        SerializedColumn<byte[]>? _writer;

        public BinaryColumnCodec(DataFrameColumn column, bool optional, ParquetPhysicalType physicalType,
            LogicalType? logicalType)
        {
            _column = column;
            _optional = optional;
            Definition = CreateDefinition(column.Name, optional, physicalType, logicalType);
        }

        public BinaryColumnCodec(string name, long rowCount, bool optional, ParquetPhysicalType physicalType,
            LogicalType? logicalType)
        {
            _column = new BinaryDataFrameColumn(name, rowCount);
            _optional = optional;
            Definition = CreateDefinition(name, optional, physicalType, logicalType);
        }

        public override DataFrameColumn Column
            => _column;

        public override ColumnDefinition Definition { get; }

        public override void BindWriter(ParquetWriter writer, LeafColumn leaf)
            => _writer = writer.CreateSerializedColumn<byte[]>(leaf);

        public override void Write(RowGroupWriter rowGroup, long offset, int count)
        {
            var values = new byte[count][];
            for (var i = 0; i < values.Length; i++)
            {
                var value = _column[offset + i];
                if (value is null && !_optional)
                    throw new InvalidOperationException(
                        $"Required DataFrame column '{_column.Name}' contains null at row {offset + i}.");
                values[i] = (byte[]?)value!;
            }

            var writer = _writer ?? throw new InvalidOperationException("The column writer is not bound.");
            writer.Serialize(values);
            rowGroup.Write(writer);
        }

        public override void Read(RowGroup rowGroup, LeafColumn leaf, long offset)
        {
            var destination = (BinaryDataFrameColumn)_column;
            var count = 0L;
            foreach (var buffer in rowGroup.Column<byte>(leaf))
                for (var i = 0; i < buffer.Count; i++)
                    destination[offset + count++] = buffer.IsNull(i) ? null : buffer.GetValue(i).ToArray();
            ValidateReadCount(rowGroup, leaf, count);
        }
    }

    sealed class GuidColumnCodec : DataFrameColumnCodec
    {
        readonly DataFrameColumn _column;
        readonly bool _optional;
        SerializedColumn<Guid>? _requiredWriter;
        SerializedColumn<Guid?>? _optionalWriter;

        public GuidColumnCodec(DataFrameColumn column, bool optional)
        {
            _column = column;
            _optional = optional;
            Definition = CreateDefinition(column.Name, optional, ParquetPhysicalType.FixedLenByteArray,
                new LogicalType.Uuid(), typeLength: 16);
        }

        public GuidColumnCodec(string name, long rowCount, bool optional)
        {
            _column = new PrimitiveDataFrameColumn<Guid>(name, rowCount);
            _optional = optional;
            Definition = CreateDefinition(name, optional, ParquetPhysicalType.FixedLenByteArray,
                new LogicalType.Uuid(), typeLength: 16);
        }

        public override DataFrameColumn Column
            => _column;

        public override ColumnDefinition Definition { get; }

        public override void BindWriter(ParquetWriter writer, LeafColumn leaf)
        {
            if (_optional)
                _optionalWriter = writer.CreateSerializedColumn<Guid?>(leaf);
            else
                _requiredWriter = writer.CreateSerializedColumn<Guid>(leaf);
        }

        public override void Write(RowGroupWriter rowGroup, long offset, int count)
        {
            if (_optional)
            {
                var values = new Guid?[count];
                for (var i = 0; i < values.Length; i++)
                    if (_column[offset + i] is Guid value)
                        values[i] = value;
                var writer = _optionalWriter ?? throw new InvalidOperationException("The column writer is not bound.");
                writer.Serialize(values);
                rowGroup.Write(writer);
                return;
            }

            var required = new Guid[count];
            for (var i = 0; i < required.Length; i++)
                required[i] = _column[offset + i] is Guid value
                    ? value
                    : throw new InvalidOperationException(
                        $"Required DataFrame column '{_column.Name}' contains null at row {offset + i}.");
            var requiredWriter = _requiredWriter ?? throw new InvalidOperationException("The column writer is not bound.");
            requiredWriter.Serialize(required);
            rowGroup.Write(requiredWriter);
        }

        public override void Read(RowGroup rowGroup, LeafColumn leaf, long offset)
        {
            var destination = (PrimitiveDataFrameColumn<Guid>)_column;
            var count = 0L;
            foreach (var buffer in rowGroup.Column<byte>(leaf))
                for (var i = 0; i < buffer.Count; i++)
                {
                    if (buffer.IsNull(i))
                    {
                        destination[offset + count++] = null;
                        continue;
                    }

                    var bytes = buffer.GetValue(i);
                    if (bytes.Length != 16)
                        throw new InvalidDataException(
                            $"UUID column '{leaf.Path}' contains a {bytes.Length}-byte value; expected 16 bytes.");
                    destination[offset + count++] = new Guid(bytes, bigEndian: true);
                }
            ValidateReadCount(rowGroup, leaf, count);
        }
    }

    static void ValidateReadCount(RowGroup rowGroup, LeafColumn leaf, long actual)
    {
        var expected = checked((long)rowGroup.RowCount);
        if (actual != expected)
            throw new InvalidDataException(
                $"Column '{leaf.Path}' produced {actual} DataFrame values for a row group containing {expected} rows.");
    }
}
