using System.Collections.Generic;
using System.Buffers;
using Plank.Schema;
using Plank.Writing.Encoding;
using Plank.Writing.PageStrategy;

namespace Plank.Writing;

public sealed class SerializedColumn
{
    static readonly DateOnly UnixEpochDate = new(1970, 1, 1);

    readonly ParquetWriter _owner;
    object? _dictionaryState;
    
    internal readonly PageList Pages;
    internal uint ColumnOrdinal;
    internal int RowCount;
    internal bool HasPendingData;

    internal SerializedColumn(ParquetWriter owner, uint initialPageCapacity)
    {
        ArgumentNullException.ThrowIfNull(owner);
        Pages = new PageList(initialPageCapacity);
        _owner = owner;
        ColumnOrdinal = 0;
        RowCount = 0;
        HasPendingData = false;
    }

    public void Serialize(Column column, ReadOnlySpan<bool> values)
        => SerializeTyped(column, values);

    public void Serialize(Column column, ReadOnlySpan<int> values)
        => SerializeTyped(column, values);

    public void Serialize(Column column, ReadOnlySpan<long> values)
        => SerializeTyped(column, values);

    public void Serialize(Column column, ReadOnlySpan<float> values)
        => SerializeTyped(column, values);

    public void Serialize(Column column, ReadOnlySpan<double> values)
        => SerializeTyped(column, values);

    public void Serialize(Column column, ReadOnlySpan<byte[]> values)
        => SerializeTyped(column, values);

    public void Serialize(Column column, ReadOnlySpan<ReadOnlyMemory<byte>> values)
        => SerializeTyped(column, values);

    public void Serialize(Column column, ReadOnlySpan<string> values)
        => SerializeTyped(column, values);

    public void Serialize(Column column, ReadOnlySpan<DateOnly> values)
    {
        RequireDateLogicalType(column);
        var rented = ArrayPool<int>.Shared.Rent(values.Length);
        try
        {
            var converted = rented.AsSpan(0, values.Length);
            for (var i = 0; i < values.Length; i++)
                converted[i] = values[i].DayNumber - UnixEpochDate.DayNumber;
            SerializeTyped(column, converted);
        }
        finally
        {
            ArrayPool<int>.Shared.Return(rented);
        }
    }

    public void Serialize(Column column, ReadOnlySpan<DateTime> values)
    {
        var timestamp = RequireTimestampLogicalType(column);
        var rented = ArrayPool<long>.Shared.Rent(values.Length);
        try
        {
            var converted = rented.AsSpan(0, values.Length);
            for (var i = 0; i < values.Length; i++)
                converted[i] = ToUnixTime(values[i], timestamp.Unit);
            SerializeTyped(column, converted);
        }
        finally
        {
            ArrayPool<long>.Shared.Return(rented);
        }
    }

    public void Serialize(Column column, ReadOnlySpan<DateTimeOffset> values)
    {
        var timestamp = RequireTimestampLogicalType(column);
        var rented = ArrayPool<long>.Shared.Rent(values.Length);
        try
        {
            var converted = rented.AsSpan(0, values.Length);
            for (var i = 0; i < values.Length; i++)
                converted[i] = ToUnixTime(values[i], timestamp.Unit);
            SerializeTyped(column, converted);
        }
        finally
        {
            ArrayPool<long>.Shared.Return(rented);
        }
    }

    public void Serialize(Column column, ReadOnlySpan<TimeOnly> values)
    {
        var time = RequireTimeLogicalType(column);
        var rented = ArrayPool<long>.Shared.Rent(values.Length);
        try
        {
            var converted = rented.AsSpan(0, values.Length);
            for (var i = 0; i < values.Length; i++)
                converted[i] = ToTimeValue(values[i], time.Unit);
            SerializeTyped(column, converted);
        }
        finally
        {
            ArrayPool<long>.Shared.Return(rented);
        }
    }

    public void Serialize(Column column, ReadOnlyMemory<byte>[] values)
        => SerializeTyped(column, values);

    public void Serialize(Column column, string[] values)
        => SerializeTyped(column, values);

    void SerializeTyped<T>(Column column, ReadOnlySpan<T> values)
        where T : notnull
    {
        var columnOrdinal = _owner.GetColumnOrdinal(column);
        SerializeCore(column, values, columnOrdinal, _owner.GetPageStrategy(columnOrdinal));
    }

    void SerializeCore<T>(Column column, ReadOnlySpan<T> values, uint columnOrdinal, IPageStrategy strategy)
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(column);
        ArgumentNullException.ThrowIfNull(strategy);
        if (HasPendingData)
            throw new InvalidOperationException(
                "SerializedColumn already contains pending data. Call RowGroupWriter.Write(serialized) before Serialize(...) again.");
        Pages.Clear();
        ColumnOrdinal = columnOrdinal;
        RowCount = values.Length;
        HasPendingData = true;

        Plank.Writing.Encoding.Encoding.Encode(_owner.BufferWriters, column, values, strategy, Pages,
            _owner.ColumnProjectionInfosByOrdinal[columnOrdinal], GetOrCreateDictionaryState<T>());
    }

    /// <summary>
    /// Invalidates the current prepared payload to avoid missuses.
    /// </summary>
    internal void Consume()
    {
        HasPendingData = false;
    }

    ReusableDictionaryState<T> GetOrCreateDictionaryState<T>()
        where T : notnull
    {
        if (_dictionaryState is ReusableDictionaryState<T> state)
            return state;

        state = new ReusableDictionaryState<T>();
        _dictionaryState = state;
        return state;
    }

    static long ToUnixMicroseconds(DateTime value)
    {
        if (value.Kind != DateTimeKind.Utc)
            throw new InvalidOperationException(
                $"DateTime values must have kind '{DateTimeKind.Utc}', got '{value.Kind}'.");

        return ToUnixTimeFromTicks(value.Ticks, TimeUnit.Micros);
    }

    static long ToUnixTime(DateTime value, TimeUnit unit)
    {
        if (value.Kind != DateTimeKind.Utc)
            throw new InvalidOperationException(
                $"DateTime values must have kind '{DateTimeKind.Utc}', got '{value.Kind}'.");

        return ToUnixTimeFromTicks(value.Ticks, unit);
    }

    static long ToUnixTime(DateTimeOffset value, TimeUnit unit)
        => ToUnixTimeFromTicks(value.UtcDateTime.Ticks, unit);

    static long ToUnixTimeFromTicks(long ticks, TimeUnit unit)
    {
        var deltaTicks = ticks - DateTime.UnixEpoch.Ticks;
        return unit switch
        {
            TimeUnit.Millis => deltaTicks / TimeSpan.TicksPerMillisecond,
            TimeUnit.Micros => deltaTicks / 10,
            TimeUnit.Nanos => checked(deltaTicks * 100),
            _ => throw new ArgumentOutOfRangeException(nameof(unit), unit, "Time unit must be a defined TimeUnit value.")
        };
    }

    static long ToTimeValue(TimeOnly value, TimeUnit unit)
        => unit switch
        {
            TimeUnit.Millis => value.Ticks / TimeSpan.TicksPerMillisecond,
            TimeUnit.Micros => value.Ticks / 10,
            TimeUnit.Nanos => checked(value.Ticks * 100),
            _ => throw new ArgumentOutOfRangeException(nameof(unit), unit, "Time unit must be a defined TimeUnit value.")
        };

    static void RequireDateLogicalType(Column column)
    {
        if (column.LogicalType is LogicalType.Date)
            return;

        throw new InvalidOperationException(
            $"Column '{column.Name}' must declare logical type '{typeof(LogicalType.Date)}' for DateOnly serialization.");
    }

    static LogicalType.Time RequireTimeLogicalType(Column column)
    {
        if (column.LogicalType is LogicalType.Time time)
            return time;

        throw new InvalidOperationException(
            $"Column '{column.Name}' must declare logical type '{typeof(LogicalType.Time)}' for TimeOnly serialization.");
    }

    static LogicalType.Timestamp RequireTimestampLogicalType(Column column)
    {
        if (column.LogicalType is LogicalType.Timestamp timestamp)
            return timestamp;

        throw new InvalidOperationException(
            $"Column '{column.Name}' must declare logical type '{typeof(LogicalType.Timestamp)}' for timestamp serialization.");
    }
}
