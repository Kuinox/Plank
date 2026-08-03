using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using Plank.Schema;
using Plank.Writing;

namespace Plank.Untyped;

/// <summary>Writes dictionary-based rows using an explicit runtime Parquet schema.</summary>
public sealed class ParquetUntypedWriter
{
    delegate void ColumnWriter(ParquetWriter writer, RowGroupWriter rowGroup, LeafColumn column, Array values);

    static readonly MethodInfo _writeColumnMethod = typeof(ParquetUntypedWriter)
        .GetMethod(nameof(WriteColumn), BindingFlags.NonPublic | BindingFlags.Static)!;
    static readonly ConcurrentDictionary<Type, ColumnWriter> _columnWriters = new();

    readonly ParquetWriter _writer;
    readonly UntypedSchemaPlan _plan;
    bool _closed;

    public ParquetUntypedWriter(Stream stream, ParquetSchema schema, ParquetWriterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(schema);
        _plan = new UntypedSchemaPlan(schema);
        var leaves = _plan.Leaves;
        for (var i = 0; i < leaves.Length; i++)
            ValidateWritableShape(leaves[i]);
        _writer = schema.CreateWriter(stream, options);
    }

    public ParquetSchema Schema
        => _plan.Schema;

    public void WriteRowGroup(IEnumerable<IReadOnlyDictionary<string, object?>> rows)
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        ArgumentNullException.ThrowIfNull(rows);
        var canonicalRows = rows.Select(_plan.CanonicalizeRow).ToArray();
        var leaves = _plan.Leaves;
        if (leaves.IsEmpty)
        {
            if (canonicalRows.Length != 0)
                throw new NotSupportedException("Rows without leaf columns cannot be represented by the untyped writer.");
            _writer.StartRowGroup();
            return;
        }

        var columns = new Array[leaves.Length];
        var rowTypes = new Type[leaves.Length];
        for (var columnOrdinal = 0; columnOrdinal < leaves.Length; columnOrdinal++)
        {
            var leaf = leaves[columnOrdinal];
            var scalarType = GetScalarType(leaf.Column, leaf.RepeatedDepth);
            var rowType = GetValueType(scalarType, leaf.ScalarNullable, leaf.RepeatedDepth);
            var values = Array.CreateInstance(rowType, canonicalRows.Length);
            for (var rowIndex = 0; rowIndex < canonicalRows.Length; rowIndex++)
            {
                var extracted = _plan.ExtractLeaf(canonicalRows[rowIndex], leaf);
                values.SetValue(ConvertValue(extracted, scalarType, leaf.ScalarNullable, leaf.RepeatedDepth,
                    leaf.Column, leaf.Column.Path), rowIndex);
            }
            columns[columnOrdinal] = values;
            rowTypes[columnOrdinal] = rowType;
        }

        var rowGroup = _writer.StartRowGroup();
        for (var i = 0; i < leaves.Length; i++)
            GetColumnWriter(rowTypes[i])(_writer, rowGroup, leaves[i].Column, columns[i]);
    }

    public void CloseFile()
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        _writer.CloseFile();
        _closed = true;
    }

    static ColumnWriter GetColumnWriter(Type rowType)
        => _columnWriters.GetOrAdd(rowType, static type =>
            (ColumnWriter)_writeColumnMethod.MakeGenericMethod(type).CreateDelegate(typeof(ColumnWriter)));

    static void WriteColumn<T>(ParquetWriter writer, RowGroupWriter rowGroup, LeafColumn column, Array values)
    {
        var serialized = writer.CreateSerializedColumn<T>(column);
        serialized.Serialize((T[])values);
        rowGroup.Write(serialized);
    }

    static object? ConvertValue(object? value, Type scalarType, bool scalarNullable, int repeatedDepth,
        LeafColumn column, string path)
    {
        if (repeatedDepth == 0)
        {
            if (value is null)
            {
                if (scalarNullable || !scalarType.IsValueType)
                    return null;
                throw new ArgumentException($"Required value '{path}' is missing or null.");
            }
            return ConvertScalar(value, scalarType, column, path);
        }

        if (value is null)
            return null;
        if (value is not object?[] source)
            throw new ArgumentException($"Repeated value '{path}' has an invalid materialized shape.");
        var elementType = GetValueType(scalarType, scalarNullable, repeatedDepth - 1);
        var result = Array.CreateInstance(elementType, source.Length);
        for (var i = 0; i < source.Length; i++)
            result.SetValue(ConvertValue(source[i], scalarType, scalarNullable, repeatedDepth - 1,
                column, $"{path}[{i}]"), i);
        return result;
    }

    static object ConvertScalar(object value, Type targetType, LeafColumn column, string path)
    {
        if (targetType.IsInstanceOfType(value))
            return value;
        if (targetType == typeof(byte[]) && (column.LogicalType is LogicalType.String or LogicalType.Json) &&
            value is string textValue)
            return System.Text.Encoding.UTF8.GetBytes(textValue);
        if (targetType == typeof(byte[]) && column.LogicalType is LogicalType.Uuid && value is Guid uuid)
        {
            var bytes = new byte[16];
            uuid.TryWriteBytes(bytes, bigEndian: true, out _);
            return bytes;
        }
        if (targetType == typeof(int) && column.LogicalType is LogicalType.Date && value is DateOnly date)
            return date.DayNumber - new DateOnly(1970, 1, 1).DayNumber;
        if (column.LogicalType is LogicalType.Time time && value is TimeOnly timeValue)
        {
            var encoded = ToTimeValue(timeValue, time.Unit);
            return targetType == typeof(int) ? checked((int)encoded) : encoded;
        }
        if (targetType == typeof(long) && column.LogicalType is LogicalType.Timestamp timestamp)
        {
            if (timestamp.IsAdjustedToUtc && value is DateTimeOffset adjusted)
                return ToTimestampValue(adjusted.UtcDateTime.Ticks, timestamp.Unit);
            if (!timestamp.IsAdjustedToUtc && value is DateTime local)
            {
                if (local.Kind != DateTimeKind.Unspecified)
                    throw new ArgumentException($"Value '{path}' must have DateTimeKind.Unspecified.");
                return ToTimestampValue(local.Ticks, timestamp.Unit);
            }
        }
        if (column.LogicalType is LogicalType.Int integer && !integer.IsSigned)
        {
            if (targetType == typeof(int))
                return unchecked((int)Convert.ToUInt32(value, CultureInfo.InvariantCulture));
            if (targetType == typeof(long))
                return unchecked((long)Convert.ToUInt64(value, CultureInfo.InvariantCulture));
        }
        if (targetType == typeof(byte[]) && value is ReadOnlyMemory<byte> memory)
            return memory.ToArray();
        if (targetType == typeof(Guid) && value is string text && Guid.TryParse(text, out var guid))
            return guid;
        if (targetType == typeof(string))
            return value as string
                ?? throw new ArgumentException($"Value '{path}' must be a string.");
        if (targetType == typeof(DateOnly) || targetType == typeof(TimeOnly) ||
            targetType == typeof(DateTime) || targetType == typeof(DateTimeOffset) ||
            targetType == typeof(Guid) || targetType == typeof(byte[]))
            throw new ArgumentException($"Value '{path}' must be a {targetType.Name}.");
        try
        {
            return Convert.ChangeType(value, targetType, CultureInfo.InvariantCulture);
        }
        catch (Exception exception) when (exception is InvalidCastException or FormatException or OverflowException)
        {
            throw new ArgumentException(
                $"Value '{path}' of type '{value.GetType()}' cannot be converted to '{targetType}'.", exception);
        }
    }

    static Type GetScalarType(LeafColumn column, int repeatedDepth)
    {
        if (repeatedDepth > 0)
            return column.PhysicalType switch
            {
                ParquetPhysicalType.Boolean => typeof(bool),
                ParquetPhysicalType.Int32 => typeof(int),
                ParquetPhysicalType.Int64 => typeof(long),
                ParquetPhysicalType.Float => typeof(float),
                ParquetPhysicalType.Double => typeof(double),
                ParquetPhysicalType.ByteArray or ParquetPhysicalType.FixedLenByteArray or ParquetPhysicalType.Int96
                    => typeof(byte[]),
                _ => throw new NotSupportedException(
                    $"Column '{column.Path}' uses unsupported physical type '{column.PhysicalType}'.")
            };
        if (column.LogicalType is LogicalType.String or LogicalType.Json)
            return typeof(string);
        if (column.LogicalType is LogicalType.Uuid)
            return typeof(Guid);
        if (column.PhysicalType is ParquetPhysicalType.ByteArray
            or ParquetPhysicalType.FixedLenByteArray
            or ParquetPhysicalType.Int96)
            return typeof(byte[]);
        if (column.LogicalType is LogicalType.Date)
            return typeof(DateOnly);
        if (column.LogicalType is LogicalType.Time)
            return typeof(TimeOnly);
        if (column.LogicalType is LogicalType.Timestamp timestamp)
            return timestamp.IsAdjustedToUtc ? typeof(DateTimeOffset) : typeof(DateTime);
        if (column.LogicalType is LogicalType.Int integer && !integer.IsSigned)
            return integer.BitWidth switch
            {
                8 => typeof(byte),
                16 => typeof(ushort),
                32 => typeof(uint),
                64 => typeof(ulong),
                _ => throw new NotSupportedException(
                    $"Column '{column.Path}' uses unsupported integer width {integer.BitWidth}.")
            };
        return column.PhysicalType switch
        {
            ParquetPhysicalType.Boolean => typeof(bool),
            ParquetPhysicalType.Int32 => typeof(int),
            ParquetPhysicalType.Int64 => typeof(long),
            ParquetPhysicalType.Float => typeof(float),
            ParquetPhysicalType.Double => typeof(double),
            _ => throw new NotSupportedException(
                $"Column '{column.Path}' uses unsupported physical type '{column.PhysicalType}'.")
        };
    }

    static Type GetValueType(Type scalarType, bool scalarNullable, int repeatedDepth)
    {
        var result = scalarNullable && scalarType.IsValueType
            ? typeof(Nullable<>).MakeGenericType(scalarType)
            : scalarType;
        for (var i = 0; i < repeatedDepth; i++)
            result = result.MakeArrayType();
        return result;
    }

    static void ValidateWritableShape(UntypedSchemaPlan.LeafPlan leaf)
    {
        var definitionLevel = 0;
        var lastRepeatedStep = -1;
        for (var i = 0; i < leaf.Steps.Length; i++)
            if (leaf.Steps[i].Kind is UntypedSchemaPlan.StepKind.List or UntypedSchemaPlan.StepKind.Map)
                lastRepeatedStep = i;

        for (var i = 0; i < leaf.Steps.Length; i++)
        {
            var step = leaf.Steps[i];
            switch (step.Kind)
            {
                case UntypedSchemaPlan.StepKind.Group:
                    if (step.PresenceDefinitionLevel > definitionLevel)
                        throw new NotSupportedException(
                            $"Column '{leaf.Column.Path}' has an optional group ancestor, which Plank's column writer cannot distinguish from a null leaf value.");
                    definitionLevel = step.PresenceDefinitionLevel;
                    break;
                case UntypedSchemaPlan.StepKind.List:
                case UntypedSchemaPlan.StepKind.Map:
                    if (step.PresenceDefinitionLevel > definitionLevel && step.RepetitionDepth > 1)
                        throw new NotSupportedException(
                            $"Column '{leaf.Column.Path}' has an optional nested repeated container, which Plank's column writer does not support.");
                    definitionLevel = step.EntryDefinitionLevel;
                    break;
                case UntypedSchemaPlan.StepKind.Leaf:
                    if (step.PresenceDefinitionLevel > definitionLevel && leaf.RepeatedDepth > 1)
                        throw new NotSupportedException(
                            $"Column '{leaf.Column.Path}' has optional nested repeated elements, which Plank's column writer does not support.");
                    if (step.PresenceDefinitionLevel > definitionLevel && leaf.RepeatedDepth == 1 &&
                        i - 1 != lastRepeatedStep)
                        throw new NotSupportedException(
                            $"Column '{leaf.Column.Path}' has an optional member inside a repeated group, which Plank's column writer does not support.");
                    definitionLevel = step.PresenceDefinitionLevel;
                    break;
            }
        }
    }

    static long ToTimestampValue(long ticks, TimeUnit unit)
    {
        var deltaTicks = ticks - DateTime.UnixEpoch.Ticks;
        return unit switch
        {
            TimeUnit.Millis => DivideFloor(deltaTicks, TimeSpan.TicksPerMillisecond),
            TimeUnit.Micros => DivideFloor(deltaTicks, 10),
            TimeUnit.Nanos => checked(deltaTicks * 100),
            _ => throw new ArgumentOutOfRangeException(nameof(unit), unit, "Time unit must be defined.")
        };
    }

    static long ToTimeValue(TimeOnly value, TimeUnit unit)
        => unit switch
        {
            TimeUnit.Millis => value.Ticks / TimeSpan.TicksPerMillisecond,
            TimeUnit.Micros => value.Ticks / 10,
            TimeUnit.Nanos => checked(value.Ticks * 100),
            _ => throw new ArgumentOutOfRangeException(nameof(unit), unit, "Time unit must be defined.")
        };

    static long DivideFloor(long dividend, long divisor)
    {
        var quotient = Math.DivRem(dividend, divisor, out var remainder);
        return remainder < 0 ? quotient - 1 : quotient;
    }
}
