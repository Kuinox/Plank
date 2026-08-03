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

    uint RowCount { get; }

    ColumnStatistics Statistics { get; }

    ReadOnlySpan<byte> BloomFilterBitset { get; }

    bool HasPendingData { get; }

    void Consume();

    void CompleteBloomFilterWrite();

    void ReleaseBuffers();
}

public sealed class SerializedColumn<T> : ISerializedColumn
{
    static readonly DateOnly UnixEpochDate = new(1970, 1, 1);

    delegate ColumnStatistics PageStatisticsFactory<TValue>(ReadOnlySpan<TValue> values, long nullCount);

    internal readonly ParquetWriter _owner;
    readonly LeafColumn _leafColumn;
    readonly Column _column;
    object? _dictionaryState;
    ParquetBuffer _statisticsMinValueBuffer;
    ParquetBuffer _statisticsMaxValueBuffer;
    ParquetBuffer _bloomFilterBuffer;
    int _bloomFilterByteLength;
    bool _bloomFilterRetained;
    internal RepeatedRowShape[]? MapRowShapes;

    internal SerializedColumn(ParquetWriter owner, LeafColumn column, uint initialPageCapacity)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(column);
        _owner = owner;
        _ = owner.GetColumnOrdinal(column);
        _leafColumn = column;
        _column = column.Column;
        Pages = new PageList(initialPageCapacity);
        ColumnOrdinal = 0;
        RowCount = 0;
        HasPendingData = false;
    }

    internal PageList Pages { get; }

    internal uint ColumnOrdinal { get; private set; }

    internal uint RowCount { get; private set; }

    internal ColumnStatistics Statistics { get; private set; }

    internal bool HasPendingData { get; private set; }

    PageList ISerializedColumn.Pages => Pages;

    uint ISerializedColumn.ColumnOrdinal => ColumnOrdinal;

    uint ISerializedColumn.RowCount => RowCount;

    ColumnStatistics ISerializedColumn.Statistics => Statistics;

    ReadOnlySpan<byte> ISerializedColumn.BloomFilterBitset
        => _bloomFilterBuffer.Span[.._bloomFilterByteLength];

    bool ISerializedColumn.HasPendingData => HasPendingData;

    public void Serialize(ReadOnlySpan<T> values)
    {
        if (_bloomFilterRetained)
            throw new InvalidOperationException(
                "SerializedColumn's Bloom filter is retained by an incomplete row group.");

        if (_column.Options.Repetition == ParquetRepetition.Repeated)
        {
            SerializeRepeated(values);
            return;
        }

        if (typeof(T) == typeof(bool?))
        {
            SerializeOptionalTyped(AsNullableSpan<bool>(values));
            return;
        }

        if (typeof(T) == typeof(bool))
        {
            SerializeTyped(AsSpan<bool>(values));
            return;
        }

        if (typeof(T) == typeof(int?))
        {
            SerializeOptionalTyped(AsNullableSpan<int>(values));
            return;
        }

        if (typeof(T) == typeof(int))
        {
            SerializeTyped(AsSpan<int>(values));
            return;
        }

        if (typeof(T) == typeof(byte?))
        {
            SerializeNullableByte(AsNullableSpan<byte>(values));
            return;
        }

        if (typeof(T) == typeof(byte))
        {
            SerializeByte(AsSpan<byte>(values));
            return;
        }

        if (typeof(T) == typeof(ushort?))
        {
            SerializeNullableUInt16(AsNullableSpan<ushort>(values));
            return;
        }

        if (typeof(T) == typeof(ushort))
        {
            SerializeUInt16(AsSpan<ushort>(values));
            return;
        }

        if (typeof(T) == typeof(uint?))
        {
            SerializeNullableUInt32(AsNullableSpan<uint>(values));
            return;
        }

        if (typeof(T) == typeof(uint))
        {
            SerializeUInt32(AsSpan<uint>(values));
            return;
        }

        if (typeof(T) == typeof(long?))
        {
            SerializeOptionalTyped(AsNullableSpan<long>(values));
            return;
        }

        if (typeof(T) == typeof(long))
        {
            SerializeTyped(AsSpan<long>(values));
            return;
        }

        if (typeof(T) == typeof(ulong?))
        {
            SerializeNullableUInt64(AsNullableSpan<ulong>(values));
            return;
        }

        if (typeof(T) == typeof(ulong))
        {
            SerializeUInt64(AsSpan<ulong>(values));
            return;
        }

        if (typeof(T) == typeof(float?))
        {
            SerializeOptionalTyped(AsNullableSpan<float>(values));
            return;
        }

        if (typeof(T) == typeof(float))
        {
            SerializeTyped(AsSpan<float>(values));
            return;
        }

        if (typeof(T) == typeof(double?))
        {
            SerializeOptionalTyped(AsNullableSpan<double>(values));
            return;
        }

        if (typeof(T) == typeof(double))
        {
            SerializeTyped(AsSpan<double>(values));
            return;
        }

        if (typeof(T) == typeof(decimal?))
        {
            ParquetDecimalConverter.RequireLogicalType(_column);
            SerializeOptionalTyped(AsNullableSpan<decimal>(values));
            return;
        }

        if (typeof(T) == typeof(decimal))
        {
            ParquetDecimalConverter.RequireLogicalType(_column);
            SerializeTyped(AsSpan<decimal>(values));
            return;
        }

        if (typeof(T) == typeof(byte[]))
        {
            if (_column.Options.Repetition == ParquetRepetition.Optional)
                SerializeOptionalReference(AsAnySpan<byte[]>(values));
            else
                SerializeTyped(AsAnySpan<byte[]>(values));
            return;
        }

        if (typeof(T) == typeof(ReadOnlyMemory<byte>?))
        {
            SerializeOptionalTyped(AsNullableSpan<ReadOnlyMemory<byte>>(values));
            return;
        }

        if (typeof(T) == typeof(ReadOnlyMemory<byte>))
        {
            SerializeTyped(AsAnySpan<ReadOnlyMemory<byte>>(values));
            return;
        }

        if (typeof(T) == typeof(string))
        {
            SerializeStrings(AsAnySpan<string>(values));
            return;
        }

        if (typeof(T) == typeof(Guid?))
        {
            SerializeNullableGuids(AsNullableSpan<Guid>(values));
            return;
        }

        if (typeof(T) == typeof(Guid))
        {
            SerializeGuids(AsSpan<Guid>(values));
            return;
        }

        if (typeof(T) == typeof(DateOnly?))
        {
            SerializeNullableDateOnly(AsNullableSpan<DateOnly>(values));
            return;
        }

        if (typeof(T) == typeof(DateOnly))
        {
            SerializeDateOnly(AsSpan<DateOnly>(values));
            return;
        }

        if (typeof(T) == typeof(DateTime?))
        {
            SerializeNullableDateTime(AsNullableSpan<DateTime>(values));
            return;
        }

        if (typeof(T) == typeof(DateTime))
        {
            SerializeDateTime(AsSpan<DateTime>(values));
            return;
        }

        if (typeof(T) == typeof(DateTimeOffset?))
        {
            SerializeNullableDateTimeOffset(AsNullableSpan<DateTimeOffset>(values));
            return;
        }

        if (typeof(T) == typeof(DateTimeOffset))
        {
            SerializeDateTimeOffset(AsSpan<DateTimeOffset>(values));
            return;
        }

        if (typeof(T) == typeof(TimeOnly?))
        {
            SerializeNullableTimeOnly(AsNullableSpan<TimeOnly>(values));
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

    void SerializeStrings(ReadOnlySpan<string> values)
    {
        var encoded = new byte[values.Length][];
        for (var i = 0; i < values.Length; i++)
            if (values[i] is { } value)
                encoded[i] = System.Text.Encoding.UTF8.GetBytes(value);

        if (_column.Options.Repetition == ParquetRepetition.Optional)
            SerializeOptionalReference(encoded);
        else
            SerializeTyped(encoded);
    }

    void SerializeGuids(ReadOnlySpan<Guid> values)
    {
        RequireUuidLogicalType(_column);
        SerializeTyped(values);
    }

    void SerializeNullableGuids(ReadOnlySpan<Guid?> values)
    {
        RequireUuidLogicalType(_column);
        SerializeOptionalTyped(values);
    }

    void SerializeDateOnly(ReadOnlySpan<DateOnly> values)
    {
        RequireDateLogicalType(_column);
        var rented = _owner.BufferWriters.RentScratch<int>(checked((uint)values.Length));
        try
        {
            var converted = ParquetBuffer.AsSpan<int>(rented, values.Length);
            for (var i = 0; i < values.Length; i++)
                converted[i] = values[i].DayNumber - UnixEpochDate.DayNumber;
            SerializeTyped(converted);
        }
        finally
        {
            _owner.BufferWriters.ReturnScratch(rented);
        }
    }

    void SerializeNullableDateOnly(ReadOnlySpan<DateOnly?> values)
    {
        RequireDateLogicalType(_column);
        var rented = _owner.BufferWriters.RentScratch<int?>(checked((uint)values.Length));
        try
        {
            var converted = ParquetBuffer.AsSpan<int?>(rented, values.Length);
            for (var i = 0; i < values.Length; i++)
                converted[i] = values[i] is { } value ? value.DayNumber - UnixEpochDate.DayNumber : null;
            SerializeOptionalTyped(converted);
        }
        finally
        {
            _owner.BufferWriters.ReturnScratch(rented);
        }
    }

    void SerializeByte(ReadOnlySpan<byte> values)
    {
        var rented = _owner.BufferWriters.RentScratch<int>(checked((uint)values.Length));
        try
        {
            var converted = ParquetBuffer.AsSpan<int>(rented, values.Length);
            for (var i = 0; i < values.Length; i++)
                converted[i] = values[i];
            SerializeTyped(converted);
            Statistics = ColumnStatistics.CreateByte(values, 0);
            AssignBytePageStatistics(values);
        }
        finally
        {
            _owner.BufferWriters.ReturnScratch(rented);
        }
    }

    void SerializeNullableByte(ReadOnlySpan<byte?> values)
    {
        var rented = _owner.BufferWriters.RentScratch<int?>(checked((uint)values.Length));
        try
        {
            var converted = ParquetBuffer.AsSpan<int?>(rented, values.Length);
            for (var i = 0; i < values.Length; i++)
                converted[i] = values[i];
            SerializeOptionalTyped(converted);
            Statistics = ColumnStatistics.CreateNullableByte(values);
            AssignNullableBytePageStatistics(values);
        }
        finally
        {
            _owner.BufferWriters.ReturnScratch(rented);
        }
    }

    void SerializeUInt16(ReadOnlySpan<ushort> values)
    {
        var rented = _owner.BufferWriters.RentScratch<int>(checked((uint)values.Length));
        try
        {
            var converted = ParquetBuffer.AsSpan<int>(rented, values.Length);
            for (var i = 0; i < values.Length; i++)
                converted[i] = values[i];
            SerializeTyped(converted);
            Statistics = ColumnStatistics.CreateUInt16(values, 0);
            AssignUInt16PageStatistics(values);
        }
        finally
        {
            _owner.BufferWriters.ReturnScratch(rented);
        }
    }

    void SerializeNullableUInt16(ReadOnlySpan<ushort?> values)
    {
        var rented = _owner.BufferWriters.RentScratch<int?>(checked((uint)values.Length));
        try
        {
            var converted = ParquetBuffer.AsSpan<int?>(rented, values.Length);
            for (var i = 0; i < values.Length; i++)
                converted[i] = values[i];
            SerializeOptionalTyped(converted);
            Statistics = ColumnStatistics.CreateNullableUInt16(values);
            AssignNullableUInt16PageStatistics(values);
        }
        finally
        {
            _owner.BufferWriters.ReturnScratch(rented);
        }
    }

    void SerializeUInt32(ReadOnlySpan<uint> values)
    {
        var rented = _owner.BufferWriters.RentScratch<int>(checked((uint)values.Length));
        try
        {
            var converted = ParquetBuffer.AsSpan<int>(rented, values.Length);
            for (var i = 0; i < values.Length; i++)
                converted[i] = unchecked((int)values[i]);
            SerializeTyped(converted);
            Statistics = ColumnStatistics.CreateUInt32(values, 0);
            AssignUInt32PageStatistics(values);
        }
        finally
        {
            _owner.BufferWriters.ReturnScratch(rented);
        }
    }

    void SerializeNullableUInt32(ReadOnlySpan<uint?> values)
    {
        var rented = _owner.BufferWriters.RentScratch<int?>(checked((uint)values.Length));
        try
        {
            var converted = ParquetBuffer.AsSpan<int?>(rented, values.Length);
            for (var i = 0; i < values.Length; i++)
                converted[i] = values[i] is { } value ? unchecked((int)value) : null;
            SerializeOptionalTyped(converted);
            Statistics = ColumnStatistics.CreateNullableUInt32(values);
            AssignNullableUInt32PageStatistics(values);
        }
        finally
        {
            _owner.BufferWriters.ReturnScratch(rented);
        }
    }

    void SerializeDateTime(ReadOnlySpan<DateTime> values)
    {
        var timestamp = RequireTimestampLogicalType(_column);
        var rented = _owner.BufferWriters.RentScratch<long>(checked((uint)values.Length));
        try
        {
            var converted = ParquetBuffer.AsSpan<long>(rented, values.Length);
            for (var i = 0; i < values.Length; i++)
                converted[i] = ToUnixTime(values[i], timestamp);
            SerializeTyped(converted);
        }
        finally
        {
            _owner.BufferWriters.ReturnScratch(rented);
        }
    }

    void SerializeNullableDateTime(ReadOnlySpan<DateTime?> values)
    {
        var timestamp = RequireTimestampLogicalType(_column);
        var rented = _owner.BufferWriters.RentScratch<long?>(checked((uint)values.Length));
        try
        {
            var converted = ParquetBuffer.AsSpan<long?>(rented, values.Length);
            for (var i = 0; i < values.Length; i++)
                converted[i] = values[i] is { } value ? ToUnixTime(value, timestamp) : null;
            SerializeOptionalTyped(converted);
        }
        finally
        {
            _owner.BufferWriters.ReturnScratch(rented);
        }
    }

    void SerializeUInt64(ReadOnlySpan<ulong> values)
    {
        var rented = _owner.BufferWriters.RentScratch<long>(checked((uint)values.Length));
        try
        {
            var converted = ParquetBuffer.AsSpan<long>(rented, values.Length);
            for (var i = 0; i < values.Length; i++)
                converted[i] = unchecked((long)values[i]);
            SerializeTyped(converted);
            Statistics = ColumnStatistics.CreateUInt64(values, 0);
            AssignUInt64PageStatistics(values);
        }
        finally
        {
            _owner.BufferWriters.ReturnScratch(rented);
        }
    }

    void SerializeNullableUInt64(ReadOnlySpan<ulong?> values)
    {
        var rented = _owner.BufferWriters.RentScratch<long?>(checked((uint)values.Length));
        try
        {
            var converted = ParquetBuffer.AsSpan<long?>(rented, values.Length);
            for (var i = 0; i < values.Length; i++)
                converted[i] = values[i] is { } value ? unchecked((long)value) : null;
            SerializeOptionalTyped(converted);
            Statistics = ColumnStatistics.CreateNullableUInt64(values);
            AssignNullableUInt64PageStatistics(values);
        }
        finally
        {
            _owner.BufferWriters.ReturnScratch(rented);
        }
    }

    void SerializeDateTimeOffset(ReadOnlySpan<DateTimeOffset> values)
    {
        var timestamp = RequireAdjustedTimestampLogicalType(_column);
        var rented = _owner.BufferWriters.RentScratch<long>(checked((uint)values.Length));
        try
        {
            var converted = ParquetBuffer.AsSpan<long>(rented, values.Length);
            for (var i = 0; i < values.Length; i++)
                converted[i] = ToUnixTime(values[i], timestamp.Unit);
            SerializeTyped(converted);
        }
        finally
        {
            _owner.BufferWriters.ReturnScratch(rented);
        }
    }

    void SerializeNullableDateTimeOffset(ReadOnlySpan<DateTimeOffset?> values)
    {
        var timestamp = RequireAdjustedTimestampLogicalType(_column);
        var rented = _owner.BufferWriters.RentScratch<long?>(checked((uint)values.Length));
        try
        {
            var converted = ParquetBuffer.AsSpan<long?>(rented, values.Length);
            for (var i = 0; i < values.Length; i++)
                converted[i] = values[i] is { } value ? ToUnixTime(value, timestamp.Unit) : null;
            SerializeOptionalTyped(converted);
        }
        finally
        {
            _owner.BufferWriters.ReturnScratch(rented);
        }
    }

    void SerializeTimeOnly(ReadOnlySpan<TimeOnly> values)
    {
        var time = RequireTimeLogicalType(_column);
        if (time.Unit == TimeUnit.Millis && _column.PhysicalType == ParquetPhysicalType.Int32)
        {
            var rentedMillis = _owner.BufferWriters.RentScratch<int>(checked((uint)values.Length));
            try
            {
                var convertedMillis = ParquetBuffer.AsSpan<int>(rentedMillis, values.Length);
                for (var i = 0; i < values.Length; i++)
                    convertedMillis[i] = checked((int)ToTimeValue(values[i], time.Unit));
                SerializeTyped(convertedMillis);
            }
            finally
            {
                _owner.BufferWriters.ReturnScratch(rentedMillis);
            }
            return;
        }

        var rented = _owner.BufferWriters.RentScratch<long>(checked((uint)values.Length));
        try
        {
            var converted = ParquetBuffer.AsSpan<long>(rented, values.Length);
            for (var i = 0; i < values.Length; i++)
                converted[i] = ToTimeValue(values[i], time.Unit);
            SerializeTyped(converted);
        }
        finally
        {
            _owner.BufferWriters.ReturnScratch(rented);
        }
    }

    void SerializeNullableTimeOnly(ReadOnlySpan<TimeOnly?> values)
    {
        var time = RequireTimeLogicalType(_column);
        if (time.Unit == TimeUnit.Millis && _column.PhysicalType == ParquetPhysicalType.Int32)
        {
            var rentedMillis = _owner.BufferWriters.RentScratch<int?>(checked((uint)values.Length));
            try
            {
                var convertedMillis = ParquetBuffer.AsSpan<int?>(rentedMillis, values.Length);
                for (var i = 0; i < values.Length; i++)
                    convertedMillis[i] = values[i] is { } value
                        ? checked((int)ToTimeValue(value, time.Unit))
                        : null;
                SerializeOptionalTyped(convertedMillis);
            }
            finally
            {
                _owner.BufferWriters.ReturnScratch(rentedMillis);
            }
            return;
        }

        var rented = _owner.BufferWriters.RentScratch<long?>(checked((uint)values.Length));
        try
        {
            var converted = ParquetBuffer.AsSpan<long?>(rented, values.Length);
            for (var i = 0; i < values.Length; i++)
                converted[i] = values[i] is { } value ? ToTimeValue(value, time.Unit) : null;
            SerializeOptionalTyped(converted);
        }
        finally
        {
            _owner.BufferWriters.ReturnScratch(rented);
        }
    }

    void SerializeTyped<TValue>(ReadOnlySpan<TValue> values)
        where TValue : notnull
    {
        var columnOrdinal = _owner.GetColumnOrdinal(_leafColumn);
        SerializeCore(values, columnOrdinal, _owner.GetPageStrategyContext(columnOrdinal));
    }

    void SerializeOptionalTyped<TValue>(ReadOnlySpan<TValue?> values)
        where TValue : struct
    {
        var columnOrdinal = _owner.GetColumnOrdinal(_leafColumn);
        SerializeOptionalCore(values, columnOrdinal, _owner.GetPageStrategyContext(columnOrdinal));
    }

    void SerializeOptionalReference<TValue>(ReadOnlySpan<TValue> values)
        where TValue : class
    {
        var columnOrdinal = _owner.GetColumnOrdinal(_leafColumn);
        SerializeOptionalCore(values, columnOrdinal, _owner.GetPageStrategyContext(columnOrdinal));
    }

    void SerializeRepeated(ReadOnlySpan<T> values)
    {
        var columnOrdinal = _owner.GetColumnOrdinal(_leafColumn);
#pragma warning disable CS8714
        SerializeCore(values, columnOrdinal, _owner.GetPageStrategyContext(columnOrdinal));
#pragma warning restore CS8714
        var projection = _owner.ColumnProjectionInfosByOrdinal[columnOrdinal];
        var mapProjections = projection.MapProjections;
        if (mapProjections.IsDefaultOrEmpty)
            return;

        var shapes = new RepeatedRowShape[mapProjections.Length];
        for (var i = 0; i < shapes.Length; i++)
            shapes[i] = CaptureRepeatedShape(values, mapProjections[i].RepetitionLevel);
        MapRowShapes = shapes;
    }

    static RepeatedRowShape CaptureRepeatedShape(ReadOnlySpan<T> rows, int depth)
    {
        var rowOffsets = new int[rows.Length + 1];
        var tokenCount = 0;
        for (var i = 0; i < rows.Length; i++)
        {
            rowOffsets[i] = tokenCount;
            tokenCount = checked(tokenCount + CountShapeTokens(rows[i], depth));
        }
        rowOffsets[rows.Length] = tokenCount;

        var tokens = new int[tokenCount];
        var tokenIndex = 0;
        for (var i = 0; i < rows.Length; i++)
            WriteShapeTokens(rows[i], depth, tokens, ref tokenIndex);
        return new RepeatedRowShape(tokens, rowOffsets);
    }

    static int CountShapeTokens(object? node, int depth)
    {
        if (node is null)
            return 1;
        if (node is not Array array || depth <= 0)
            throw new InvalidOperationException("Repeated column rows must have the expected jagged-array shape.");

        var count = 1;
        if (depth == 1)
            return count;
        for (var i = 0; i < array.Length; i++)
            count = checked(count + CountShapeTokens(array.GetValue(i), depth - 1));
        return count;
    }

    static void WriteShapeTokens(object? node, int depth, Span<int> destination, ref int index)
    {
        if (node is null)
        {
            destination[index++] = -1;
            return;
        }

        var array = (Array)node;
        destination[index++] = array.Length;
        if (depth == 1)
            return;
        for (var i = 0; i < array.Length; i++)
            WriteShapeTokens(array.GetValue(i), depth - 1, destination, ref index);
    }

    void SerializeCore<TValue>(ReadOnlySpan<TValue> values, uint columnOrdinal, PageStrategyContext strategyContext)
        where TValue : notnull
    {
        ArgumentNullException.ThrowIfNull(strategyContext);
        if (HasPendingData)
            throw new InvalidOperationException(
                "SerializedColumn already contains pending data. Call RowGroupWriter.Write(serialized) before Serialize(...) again.");
        Pages.Clear();
        ColumnOrdinal = columnOrdinal;
        RowCount = checked((uint)values.Length);
        HasPendingData = true;

        Plank.Writing.Encoding.Encoding.Encode(_owner.BufferWriters, _column, values, strategyContext, Pages,
            _owner.ColumnProjectionInfosByOrdinal[columnOrdinal], GetOrCreateDictionaryState<TValue>());
        _bloomFilterByteLength = BloomFilterBuilder.Build(_owner.BufferWriters, _column, values,
            ref _bloomFilterBuffer);
        if (_owner.WritePageIndexes && TryAssignInt32ColumnAndPageStatistics(values))
            return;

        Statistics = ColumnStatistics.CreateWithReusableBinaryBuffers(_column, values, 0,
            ref _statisticsMinValueBuffer, ref _statisticsMaxValueBuffer, _owner.BufferWriters.BufferPool);
        if (_owner.WritePageIndexes && !TryAssignSingleDataPageStatistics(Statistics))
            AssignPageStatistics(values);
    }

    void SerializeOptionalCore<TValue>(ReadOnlySpan<TValue?> values, uint columnOrdinal,
        PageStrategyContext strategyContext)
        where TValue : struct
    {
        ArgumentNullException.ThrowIfNull(strategyContext);
        if (HasPendingData)
            throw new InvalidOperationException(
                "SerializedColumn already contains pending data. Call RowGroupWriter.Write(serialized) before Serialize(...) again.");
        Pages.Clear();
        ColumnOrdinal = columnOrdinal;
        RowCount = checked((uint)values.Length);
        Statistics = ColumnStatistics.CreateOptionalWithReusableBinaryBuffers(_column, values,
            ref _statisticsMinValueBuffer, ref _statisticsMaxValueBuffer, _owner.BufferWriters.BufferPool);
        HasPendingData = true;

        Plank.Writing.Encoding.Encoding.EncodeOptional(_owner.BufferWriters, _column, values, strategyContext, Pages,
            _owner.ColumnProjectionInfosByOrdinal[columnOrdinal], GetOrCreateDictionaryState<TValue>());
        _bloomFilterByteLength = BloomFilterBuilder.BuildOptional(_owner.BufferWriters, _column, values,
            ref _bloomFilterBuffer);
        if (_owner.WritePageIndexes && !TryAssignSingleDataPageStatistics(Statistics))
            AssignOptionalPageStatistics(values);
    }

    void SerializeOptionalCore<TValue>(ReadOnlySpan<TValue> values, uint columnOrdinal,
        PageStrategyContext strategyContext)
        where TValue : class
    {
        ArgumentNullException.ThrowIfNull(strategyContext);
        if (HasPendingData)
            throw new InvalidOperationException(
                "SerializedColumn already contains pending data. Call RowGroupWriter.Write(serialized) before Serialize(...) again.");
        Pages.Clear();
        ColumnOrdinal = columnOrdinal;
        RowCount = checked((uint)values.Length);
        Statistics = ColumnStatistics.CreateOptionalWithReusableBinaryBuffers(_column, values,
            ref _statisticsMinValueBuffer, ref _statisticsMaxValueBuffer, _owner.BufferWriters.BufferPool);
        HasPendingData = true;

        Plank.Writing.Encoding.Encoding.EncodeOptional(_owner.BufferWriters, _column, values, strategyContext, Pages,
            _owner.ColumnProjectionInfosByOrdinal[columnOrdinal], GetOrCreateDictionaryState<TValue>());
        _bloomFilterByteLength = BloomFilterBuilder.BuildOptionalReferences(_owner.BufferWriters, _column, values,
            ref _bloomFilterBuffer);
        if (_owner.WritePageIndexes && !TryAssignSingleDataPageStatistics(Statistics))
            AssignOptionalPageStatistics(values);
    }

    void ISerializedColumn.Consume()
        => Consume();

    void ISerializedColumn.CompleteBloomFilterWrite()
        => _bloomFilterRetained = false;

    internal void Consume()
    {
        HasPendingData = false;
        _bloomFilterRetained = _bloomFilterByteLength != 0;
        MapRowShapes = null;
    }

    void ISerializedColumn.ReleaseBuffers()
        => ReleaseBuffers();

    internal void ReleaseBuffers()
    {
        Pages.ReleaseBuffers();
        _statisticsMinValueBuffer.Dispose();
        _statisticsMaxValueBuffer.Dispose();
        _bloomFilterBuffer.Dispose();
        _bloomFilterByteLength = 0;
        _bloomFilterRetained = false;
        Statistics = default;
        HasPendingData = false;
        MapRowShapes = null;
    }

    bool TryAssignSingleDataPageStatistics(ColumnStatistics statistics)
    {
        var dataPageIndex = -1;
        for (var i = 0; i < Pages.Count; i++)
        {
            ref var candidate = ref Pages[i];
            if (candidate.Kind != PageKind.DataV2)
                continue;
            if (dataPageIndex >= 0)
                return false;
            dataPageIndex = i;
        }

        if (dataPageIndex < 0)
            return false;

        ref var page = ref Pages[dataPageIndex];
        if (page.RowCount != RowCount)
            return false;

        page.Statistics = statistics;
        return true;
    }

    bool TryAssignInt32ColumnAndPageStatistics<TValue>(ReadOnlySpan<TValue> values)
        where TValue : notnull
    {
        if (_column.PhysicalType != ParquetPhysicalType.Int32 || _column.Options.Repetition != ParquetRepetition.Required)
            return false;
        if (typeof(TValue) != typeof(int))
            return false;

        var intValues = Unsafe.As<ReadOnlySpan<TValue>, ReadOnlySpan<int>>(ref values);
        var rowOffset = 0;
        var hasColumnValue = false;
        var columnMin = 0;
        var columnMax = 0;
        for (var i = 0; i < Pages.Count; i++)
        {
            ref var page = ref Pages[i];
            if (page.Kind != PageKind.DataV2)
                continue;

            var pageRowCount = checked((int)page.RowCount);
            var pageValues = intValues.Slice(rowOffset, pageRowCount);
            rowOffset += pageRowCount;
            if (pageValues.Length == 0)
            {
                page.Statistics = ColumnStatistics.Empty(page.NullCount);
                continue;
            }

            if (!ColumnStatistics.TryGetInt32MinMax(pageValues, out var pageMin, out var pageMax))
                throw new InvalidOperationException("Page statistics could not be computed for a non-empty int32 page.");
            page.Statistics = ColumnStatistics.FromInt32(pageMin, pageMax, page.NullCount);
            if (!hasColumnValue)
            {
                columnMin = pageMin;
                columnMax = pageMax;
                hasColumnValue = true;
                continue;
            }

            if (pageMin < columnMin)
                columnMin = pageMin;
            if (pageMax > columnMax)
                columnMax = pageMax;
        }

        if (rowOffset != intValues.Length)
            throw new InvalidOperationException(
                $"Int32 page statistics covered {rowOffset} rows, but the column contains {intValues.Length} rows.");

        Statistics = hasColumnValue
            ? ColumnStatistics.FromInt32(columnMin, columnMax, 0)
            : ColumnStatistics.Empty(0);
        return true;
    }

    ReusableDictionaryState<TValue> GetOrCreateDictionaryState<TValue>()
        where TValue : notnull
    {
        if (_dictionaryState is ReusableDictionaryState<TValue> state)
            return state;

        state = new ReusableDictionaryState<TValue>();
        _dictionaryState = state;
        return state;
    }

    void AssignPageStatistics<TValue>(ReadOnlySpan<TValue> values)
        where TValue : notnull
    {
        var rowOffset = 0;
        for (var i = 0; i < Pages.Count; i++)
        {
            ref var page = ref Pages[i];
            if (page.Kind != PageKind.DataV2)
                continue;
            var pageRowCount = checked((int)page.RowCount);
            var pageRows = values.Slice(rowOffset, pageRowCount);
            page.Statistics = ColumnStatistics.CreateWithReusableBinaryBuffers(_column, pageRows, page.NullCount,
                ref page.StatisticsMinValueBuffer, ref page.StatisticsMaxValueBuffer, _owner.BufferWriters.BufferPool);
            rowOffset += pageRowCount;
        }
    }

    void AssignOptionalPageStatistics<TValue>(ReadOnlySpan<TValue?> values)
        where TValue : struct
    {
        var rowOffset = 0;
        for (var i = 0; i < Pages.Count; i++)
        {
            ref var page = ref Pages[i];
            if (page.Kind != PageKind.DataV2)
                continue;
            var pageRowCount = checked((int)page.RowCount);
            var pageRows = values.Slice(rowOffset, pageRowCount);
            page.Statistics = ColumnStatistics.CreateOptionalWithReusableBinaryBuffers(_column, pageRows,
                ref page.StatisticsMinValueBuffer, ref page.StatisticsMaxValueBuffer,
                _owner.BufferWriters.BufferPool);
            rowOffset += pageRowCount;
        }
    }

    void AssignOptionalPageStatistics<TValue>(ReadOnlySpan<TValue> values)
        where TValue : class
    {
        var rowOffset = 0;
        for (var i = 0; i < Pages.Count; i++)
        {
            ref var page = ref Pages[i];
            if (page.Kind != PageKind.DataV2)
                continue;
            var pageRowCount = checked((int)page.RowCount);
            var pageRows = values.Slice(rowOffset, pageRowCount);
            page.Statistics = ColumnStatistics.CreateOptionalWithReusableBinaryBuffers(_column, pageRows,
                ref page.StatisticsMinValueBuffer, ref page.StatisticsMaxValueBuffer,
                _owner.BufferWriters.BufferPool);
            rowOffset += pageRowCount;
        }
    }

    void AssignBytePageStatistics(ReadOnlySpan<byte> values)
        => AssignConvertedPageStatistics(values, static (pageValues, nullCount) =>
            ColumnStatistics.CreateByte(pageValues, nullCount));

    void AssignNullableBytePageStatistics(ReadOnlySpan<byte?> values)
        => AssignConvertedPageStatistics(values, static (pageValues, _) =>
            ColumnStatistics.CreateNullableByte(pageValues));

    void AssignUInt16PageStatistics(ReadOnlySpan<ushort> values)
        => AssignConvertedPageStatistics(values, static (pageValues, nullCount) =>
            ColumnStatistics.CreateUInt16(pageValues, nullCount));

    void AssignNullableUInt16PageStatistics(ReadOnlySpan<ushort?> values)
        => AssignConvertedPageStatistics(values, static (pageValues, _) =>
            ColumnStatistics.CreateNullableUInt16(pageValues));

    void AssignUInt32PageStatistics(ReadOnlySpan<uint> values)
        => AssignConvertedPageStatistics(values, static (pageValues, nullCount) =>
            ColumnStatistics.CreateUInt32(pageValues, nullCount));

    void AssignNullableUInt32PageStatistics(ReadOnlySpan<uint?> values)
        => AssignConvertedPageStatistics(values, static (pageValues, _) =>
            ColumnStatistics.CreateNullableUInt32(pageValues));

    void AssignUInt64PageStatistics(ReadOnlySpan<ulong> values)
        => AssignConvertedPageStatistics(values, static (pageValues, nullCount) =>
            ColumnStatistics.CreateUInt64(pageValues, nullCount));

    void AssignNullableUInt64PageStatistics(ReadOnlySpan<ulong?> values)
        => AssignConvertedPageStatistics(values, static (pageValues, _) =>
            ColumnStatistics.CreateNullableUInt64(pageValues));

    void AssignConvertedPageStatistics<TValue>(ReadOnlySpan<TValue> values,
        PageStatisticsFactory<TValue> createStatistics)
    {
        var rowOffset = 0;
        for (var i = 0; i < Pages.Count; i++)
        {
            ref var page = ref Pages[i];
            if (page.Kind != PageKind.DataV2)
                continue;
            var pageRowCount = checked((int)page.RowCount);
            var pageRows = values.Slice(rowOffset, pageRowCount);
            page.Statistics = createStatistics(pageRows, page.NullCount);
            rowOffset += pageRowCount;
        }
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

    static ReadOnlySpan<TTo?> AsNullableSpan<TTo>(ReadOnlySpan<T> values)
        where TTo : struct
    {
        ref var first = ref Unsafe.As<T, TTo?>(ref MemoryMarshal.GetReference(values));
        return MemoryMarshal.CreateReadOnlySpan(ref first, values.Length);
    }

    static long ToUnixTime(DateTime value, LogicalType.Timestamp timestamp)
    {
        var expectedKind = timestamp.IsAdjustedToUtc ? DateTimeKind.Utc : DateTimeKind.Unspecified;
        if (value.Kind != expectedKind)
            throw new InvalidOperationException(
                $"DateTime values must have kind '{expectedKind}', got '{value.Kind}'.");

        return ToUnixTimeFromTicks(value.Ticks, timestamp.Unit);
    }

    static long ToUnixTime(DateTimeOffset value, TimeUnit unit)
        => ToUnixTimeFromTicks(value.UtcDateTime.Ticks, unit);

    static long ToUnixTimeFromTicks(long ticks, TimeUnit unit)
    {
        var deltaTicks = ticks - DateTime.UnixEpoch.Ticks;
        return unit switch
        {
            TimeUnit.Millis => TimestampConversion.DivideFloor(deltaTicks, TimeSpan.TicksPerMillisecond),
            TimeUnit.Micros => TimestampConversion.DivideFloor(deltaTicks, 10),
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

    static LogicalType.Timestamp RequireAdjustedTimestampLogicalType(Column column)
    {
        var timestamp = RequireTimestampLogicalType(column);
        if (!timestamp.IsAdjustedToUtc)
            throw new InvalidOperationException(
                $"Column '{column.Name}' must use adjusted-to-UTC timestamp semantics for DateTimeOffset serialization.");

        return timestamp;
    }

    static void RequireUuidLogicalType(Column column)
    {
        if (column.LogicalType is LogicalType.Uuid &&
            column.PhysicalType == ParquetPhysicalType.FixedLenByteArray &&
            column.Options.TypeLength == 16)
            return;

        throw new InvalidOperationException(
            $"Column '{column.Name}' must be a 16-byte fixed-length column with UUID logical type for Guid serialization.");
    }
}
