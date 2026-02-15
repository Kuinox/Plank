using Plank.Schema;

namespace Plank.Writing;

internal sealed partial class ColumnSemanticRegistry
{
    internal readonly ColumnSemanticState[] _states;
    internal readonly ColumnLogicalType[] _logicalTypes;

    internal ColumnSemanticRegistry(int columnCount)
    {
        _states = columnCount > 0 ? new ColumnSemanticState[columnCount] : [];
        _logicalTypes = columnCount > 0 ? new ColumnLogicalType[columnCount] : [];
    }

    internal void Clear()
    {
        if (_states.Length > 0)
            Array.Clear(_states);
        if (_logicalTypes.Length > 0)
            Array.Clear(_logicalTypes);
    }

    internal bool IsRepeatedElementOptional(int ordinal)
        => _states[ordinal].Repeated == RepeatedElementState.Optional;

    internal void RegisterValueType(int ordinal, Column column, Type valueType)
    {
        if (column.Options.Repetition is ParquetRepetition.Repeated)
            RegisterRepeatedSemanticState(ordinal, _states, GetRepeatedElementState(valueType), column.Name);
        switch (column.PhysicalType)
        {
            case ParquetPhysicalType.Int32:
                RegisterLogicalSemanticState(ordinal, _states, GetInt32SemanticState(valueType), column.Name);
                if (_states[ordinal].Logical == LogicalSemanticState.Int32Date)
                    _logicalTypes[ordinal] = ColumnLogicalType.Date;
                break;
            case ParquetPhysicalType.Int64:
                RegisterLogicalSemanticState(ordinal, _states, GetInt64SemanticState(valueType), column.Name);
                if (_states[ordinal].Logical == LogicalSemanticState.Int64TimestampMicrosUtc)
                    _logicalTypes[ordinal] = ColumnLogicalType.TimestampMicrosUtc;
                if (_states[ordinal].Logical == LogicalSemanticState.Int64TimeMicros)
                    _logicalTypes[ordinal] = ColumnLogicalType.TimeMicros;
                break;
            case ParquetPhysicalType.ByteArray:
                RegisterLogicalSemanticState(ordinal, _states, GetByteArraySemanticState(valueType), column.Name);
                if (_states[ordinal].Logical == LogicalSemanticState.ByteArrayUtf8)
                    _logicalTypes[ordinal] = ColumnLogicalType.Utf8;
                break;
        }
    }

    internal void RegisterSerializedColumnType(int ordinal, Column column, ColumnLogicalType logicalType, bool repeatedElementOptional)
    {
        if (column.Options.Repetition is ParquetRepetition.Repeated)
            RegisterRepeatedSemanticState(ordinal, _states, repeatedElementOptional ? RepeatedElementState.Optional : RepeatedElementState.Required, column.Name);
        switch (column.PhysicalType)
        {
            case ParquetPhysicalType.Int32:
                RegisterLogicalSemanticState(ordinal, _states, logicalType == ColumnLogicalType.Date ? LogicalSemanticState.Int32Date : LogicalSemanticState.Int32Plain, column.Name);
                break;
            case ParquetPhysicalType.Int64:
                RegisterLogicalSemanticState(ordinal, _states, GetInt64SerializedState(logicalType), column.Name);
                break;
            case ParquetPhysicalType.ByteArray:
                RegisterLogicalSemanticState(ordinal, _states, logicalType == ColumnLogicalType.Utf8 ? LogicalSemanticState.ByteArrayUtf8 : LogicalSemanticState.ByteArrayPlain, column.Name);
                break;
            default:
                return;
        }

        _logicalTypes[ordinal] = logicalType;
    }

    internal static ColumnLogicalType ResolveSerializedLogicalType(Type valueType, Column column)
    {
        if (column.PhysicalType == ParquetPhysicalType.Int64)
            return valueType == typeof(DateTime) || valueType == typeof(DateTimeOffset)
                ? ColumnLogicalType.TimestampMicrosUtc
                : valueType == typeof(TimeOnly)
                    ? ColumnLogicalType.TimeMicros
                    : ColumnLogicalType.None;
        if (column.PhysicalType == ParquetPhysicalType.Int32)
            return valueType == typeof(DateOnly)
                ? ColumnLogicalType.Date
                : ColumnLogicalType.None;
        if (column.PhysicalType == ParquetPhysicalType.ByteArray)
            return valueType == typeof(string) ? ColumnLogicalType.Utf8 : ColumnLogicalType.None;
        return ColumnLogicalType.None;
    }

    internal static bool IsRepeatedElementOptional(Type valueType)
        => GetRepeatedElementState(valueType) == RepeatedElementState.Optional;

    internal static LogicalSemanticState GetInt64SerializedState(ColumnLogicalType logicalType)
        => logicalType switch
        {
            ColumnLogicalType.TimestampMicrosUtc => LogicalSemanticState.Int64TimestampMicrosUtc,
            ColumnLogicalType.TimeMicros => LogicalSemanticState.Int64TimeMicros,
            _ => LogicalSemanticState.Int64Plain
        };

    static LogicalSemanticState GetInt32SemanticState(Type valueType)
    {
        if (valueType == typeof(DateOnly))
            return LogicalSemanticState.Int32Date;
        if (valueType == typeof(int))
            return LogicalSemanticState.Int32Plain;
        return LogicalSemanticState.None;
    }

    static LogicalSemanticState GetInt64SemanticState(Type valueType)
    {
        if (valueType == typeof(DateTime) || valueType == typeof(DateTimeOffset))
            return LogicalSemanticState.Int64TimestampMicrosUtc;
        if (valueType == typeof(TimeOnly))
            return LogicalSemanticState.Int64TimeMicros;
        if (valueType == typeof(long))
            return LogicalSemanticState.Int64Plain;
        return LogicalSemanticState.None;
    }

    static LogicalSemanticState GetByteArraySemanticState(Type valueType)
    {
        if (valueType == typeof(string))
            return LogicalSemanticState.ByteArrayUtf8;
        if (valueType == typeof(byte[]))
            return LogicalSemanticState.ByteArrayPlain;
        return LogicalSemanticState.None;
    }

    static RepeatedElementState GetRepeatedElementState(Type valueType)
    {
        if (!valueType.IsValueType)
            return RepeatedElementState.Optional;
        if (Nullable.GetUnderlyingType(valueType) is not null)
            return RepeatedElementState.Optional;
        return RepeatedElementState.Required;
    }

    static void RegisterLogicalSemanticState(int ordinal, ColumnSemanticState[] states, LogicalSemanticState desiredState, string columnName)
    {
        if (desiredState == LogicalSemanticState.None)
            return;

        var existing = states[ordinal].Logical;
        if (existing == LogicalSemanticState.None)
        {
            states[ordinal].Logical = desiredState;
            return;
        }

        if (existing != desiredState)
            throw new InvalidOperationException($"Column '{columnName}' mixes incompatible logical semantics for its physical type.");
    }

    static void RegisterRepeatedSemanticState(int ordinal, ColumnSemanticState[] states, RepeatedElementState desiredState, string columnName)
    {
        if (desiredState == RepeatedElementState.None)
            return;

        var existing = states[ordinal].Repeated;
        if (existing == RepeatedElementState.None)
        {
            states[ordinal].Repeated = desiredState;
            return;
        }

        if (existing != desiredState)
            throw new InvalidOperationException($"Column '{columnName}' mixes incompatible logical semantics for its physical type.");
    }

    internal struct ColumnSemanticState
    {
        internal LogicalSemanticState Logical;
        internal RepeatedElementState Repeated;
    }
}
