using System.Buffers;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Plank.Schema;
using Plank.Writing.Encoding;
using Plank.Writing.PageStrategy;

namespace Plank.Writing;

internal interface ISerializedColumn
{
    PageList Pages { get; }

    uint ColumnOrdinal { get; }

    int RowCount { get; }

    bool HasPendingData { get; }

    void Consume();
}

public sealed class SerializedColumn<T> : ISerializedColumn
{
    static readonly DateOnly UnixEpochDate = new(1970, 1, 1);

    readonly ParquetWriter _owner;
    readonly Column _column;
    object? _dictionaryState;

    public SerializedColumn(ParquetWriter owner, Column column, uint initialPageCapacity)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(column);
        _owner = owner;
        _column = column;
        Pages = new PageList(initialPageCapacity);
        ColumnOrdinal = 0;
        RowCount = 0;
        HasPendingData = false;
    }

    internal PageList Pages { get; }

    internal uint ColumnOrdinal { get; private set; }

    internal int RowCount { get; private set; }

    internal bool HasPendingData { get; private set; }

    PageList ISerializedColumn.Pages => Pages;

    uint ISerializedColumn.ColumnOrdinal => ColumnOrdinal;

    int ISerializedColumn.RowCount => RowCount;

    bool ISerializedColumn.HasPendingData => HasPendingData;

    public void Serialize(ReadOnlySpan<T> values)
    {
        if (_column.Options.Repetition == ParquetRepetition.Repeated)
        {
            SerializeRepeated(values);
            return;
        }

        if (typeof(T) == typeof(bool))
        {
            SerializeTyped(AsSpan<bool>(values));
            return;
        }

        if (typeof(T) == typeof(int))
        {
            SerializeTyped(AsSpan<int>(values));
            return;
        }

        if (typeof(T) == typeof(byte))
        {
            SerializeByte(AsSpan<byte>(values));
            return;
        }

        if (typeof(T) == typeof(ushort))
        {
            SerializeUInt16(AsSpan<ushort>(values));
            return;
        }

        if (typeof(T) == typeof(uint))
        {
            SerializeUInt32(AsSpan<uint>(values));
            return;
        }

        if (typeof(T) == typeof(long))
        {
            SerializeTyped(AsSpan<long>(values));
            return;
        }

        if (typeof(T) == typeof(ulong))
        {
            SerializeUInt64(AsSpan<ulong>(values));
            return;
        }

        if (typeof(T) == typeof(float))
        {
            SerializeTyped(AsSpan<float>(values));
            return;
        }

        if (typeof(T) == typeof(double))
        {
            SerializeTyped(AsSpan<double>(values));
            return;
        }

        if (typeof(T) == typeof(byte[]))
        {
            SerializeTyped(AsAnySpan<byte[]>(values));
            return;
        }

        if (typeof(T) == typeof(ReadOnlyMemory<byte>))
        {
            SerializeTyped(AsAnySpan<ReadOnlyMemory<byte>>(values));
            return;
        }

        if (typeof(T) == typeof(string))
        {
            SerializeTyped(AsAnySpan<string>(values));
            return;
        }

        if (typeof(T) == typeof(DateOnly))
        {
            SerializeDateOnly(AsSpan<DateOnly>(values));
            return;
        }

        if (typeof(T) == typeof(DateTime))
        {
            SerializeDateTime(AsSpan<DateTime>(values));
            return;
        }

        if (typeof(T) == typeof(DateTimeOffset))
        {
            SerializeDateTimeOffset(AsSpan<DateTimeOffset>(values));
            return;
        }

        if (typeof(T) == typeof(TimeOnly))
        {
            SerializeTimeOnly(AsSpan<TimeOnly>(values));
            return;
        }

        throw new NotSupportedException($"Unsupported serialized column type '{typeof(T)}'.");
    }

    public void Serialize(T[] values)
        => Serialize(values.AsSpan());

    void SerializeDateOnly(ReadOnlySpan<DateOnly> values)
    {
        RequireDateLogicalType(_column);
        var rented = ArrayPool<int>.Shared.Rent(values.Length);
        try
        {
            var converted = rented.AsSpan(0, values.Length);
            for (var i = 0; i < values.Length; i++)
                converted[i] = values[i].DayNumber - UnixEpochDate.DayNumber;
            SerializeTyped(converted);
        }
        finally
        {
            ArrayPool<int>.Shared.Return(rented);
        }
    }

    void SerializeByte(ReadOnlySpan<byte> values)
    {
        var rented = ArrayPool<int>.Shared.Rent(values.Length);
        try
        {
            var converted = rented.AsSpan(0, values.Length);
            for (var i = 0; i < values.Length; i++)
                converted[i] = values[i];
            SerializeTyped(converted);
        }
        finally
        {
            ArrayPool<int>.Shared.Return(rented);
        }
    }

    void SerializeUInt16(ReadOnlySpan<ushort> values)
    {
        var rented = ArrayPool<int>.Shared.Rent(values.Length);
        try
        {
            var converted = rented.AsSpan(0, values.Length);
            for (var i = 0; i < values.Length; i++)
                converted[i] = values[i];
            SerializeTyped(converted);
        }
        finally
        {
            ArrayPool<int>.Shared.Return(rented);
        }
    }

    void SerializeUInt32(ReadOnlySpan<uint> values)
    {
        var rented = ArrayPool<int>.Shared.Rent(values.Length);
        try
        {
            var converted = rented.AsSpan(0, values.Length);
            for (var i = 0; i < values.Length; i++)
                converted[i] = unchecked((int)values[i]);
            SerializeTyped(converted);
        }
        finally
        {
            ArrayPool<int>.Shared.Return(rented);
        }
    }

    void SerializeDateTime(ReadOnlySpan<DateTime> values)
    {
        var timestamp = RequireTimestampLogicalType(_column);
        var rented = ArrayPool<long>.Shared.Rent(values.Length);
        try
        {
            var converted = rented.AsSpan(0, values.Length);
            for (var i = 0; i < values.Length; i++)
                converted[i] = ToUnixTime(values[i], timestamp.Unit);
            SerializeTyped(converted);
        }
        finally
        {
            ArrayPool<long>.Shared.Return(rented);
        }
    }

    void SerializeUInt64(ReadOnlySpan<ulong> values)
    {
        var rented = ArrayPool<long>.Shared.Rent(values.Length);
        try
        {
            var converted = rented.AsSpan(0, values.Length);
            for (var i = 0; i < values.Length; i++)
                converted[i] = unchecked((long)values[i]);
            SerializeTyped(converted);
        }
        finally
        {
            ArrayPool<long>.Shared.Return(rented);
        }
    }

    void SerializeDateTimeOffset(ReadOnlySpan<DateTimeOffset> values)
    {
        var timestamp = RequireTimestampLogicalType(_column);
        var rented = ArrayPool<long>.Shared.Rent(values.Length);
        try
        {
            var converted = rented.AsSpan(0, values.Length);
            for (var i = 0; i < values.Length; i++)
                converted[i] = ToUnixTime(values[i], timestamp.Unit);
            SerializeTyped(converted);
        }
        finally
        {
            ArrayPool<long>.Shared.Return(rented);
        }
    }

    void SerializeTimeOnly(ReadOnlySpan<TimeOnly> values)
    {
        var time = RequireTimeLogicalType(_column);
        var rented = ArrayPool<long>.Shared.Rent(values.Length);
        try
        {
            var converted = rented.AsSpan(0, values.Length);
            for (var i = 0; i < values.Length; i++)
                converted[i] = ToTimeValue(values[i], time.Unit);
            SerializeTyped(converted);
        }
        finally
        {
            ArrayPool<long>.Shared.Return(rented);
        }
    }

    void SerializeTyped<TValue>(ReadOnlySpan<TValue> values)
        where TValue : notnull
    {
        var columnOrdinal = _owner.GetColumnOrdinal(_column);
        SerializeCore(values, columnOrdinal, _owner.GetPageStrategy(columnOrdinal));
    }

    void SerializeRepeated(ReadOnlySpan<T> values)
    {
        var columnOrdinal = _owner.GetColumnOrdinal(_column);
#pragma warning disable CS8714
        SerializeCore(values, columnOrdinal, _owner.GetPageStrategy(columnOrdinal));
#pragma warning restore CS8714
    }

    void SerializeCore<TValue>(ReadOnlySpan<TValue> values, uint columnOrdinal, IPageStrategy strategy)
        where TValue : notnull
    {
        ArgumentNullException.ThrowIfNull(strategy);
        if (HasPendingData)
            throw new InvalidOperationException(
                "SerializedColumn already contains pending data. Call RowGroupWriter.Write(serialized) before Serialize(...) again.");
        Pages.Clear();
        ColumnOrdinal = columnOrdinal;
        RowCount = values.Length;
        HasPendingData = true;

        Plank.Writing.Encoding.Encoding.Encode(_owner.BufferWriters, _column, values, strategy, Pages,
            _owner.ColumnProjectionInfosByOrdinal[columnOrdinal], GetOrCreateDictionaryState<TValue>());
    }

    void ISerializedColumn.Consume()
        => Consume();

    internal void Consume()
        => HasPendingData = false;

    ReusableDictionaryState<TValue> GetOrCreateDictionaryState<TValue>()
        where TValue : notnull
    {
        if (_dictionaryState is ReusableDictionaryState<TValue> state)
            return state;

        state = new ReusableDictionaryState<TValue>();
        _dictionaryState = state;
        return state;
    }

    static ReadOnlySpan<TTo> AsSpan<TTo>(ReadOnlySpan<T> values)
        where TTo : struct
    {
        ref var first = ref Unsafe.As<T, TTo>(ref MemoryMarshal.GetReference(values));
        return MemoryMarshal.CreateReadOnlySpan(ref first, values.Length);
    }

    static ReadOnlySpan<TTo> AsAnySpan<TTo>(ReadOnlySpan<T> values)
    {
        ref var first = ref Unsafe.As<T, TTo>(ref MemoryMarshal.GetReference(values));
        return MemoryMarshal.CreateReadOnlySpan(ref first, values.Length);
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
