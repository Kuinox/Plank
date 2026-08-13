using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Plank.Schema;
using Plank.Writing.Compression;
using Plank.Writing.Encoding;
using Plank.Writing.PageStrategy;
using Plank.Writing.Thrift;

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
    T[]? _retainedValues;
    object? _dictionaryState;
    ParquetBuffer _statisticsMinValueBuffer;
    ParquetBuffer _statisticsMaxValueBuffer;
    ParquetBuffer _bloomFilterBuffer;
    BufferWriter _compressedContent;
    BufferWriter _compressionInput;
    BufferWriter _compressedValues;
    readonly CompressionContext _compressionContext;
    int _bloomFilterByteLength;
    bool _bloomFilterRetained;
    internal RepeatedRowShape[]? MapRowShapes;

    internal SerializedColumn(ParquetWriter owner, LeafColumn column, uint initialPageCapacity, T[]? retainedValues)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(column);
        _owner = owner;
        _ = owner.GetColumnOrdinal(column);
        _leafColumn = column;
        _column = column.Column;
        _retainedValues = retainedValues;
        _compressionContext = new CompressionContext(owner.BufferWriters);
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
        SerializeValues(values);
        PreparePages();
    }

    void SerializeValues(ReadOnlySpan<T> values)
    {
        if (_bloomFilterRetained)
            throw new InvalidOperationException(
                "SerializedColumn's Bloom filter is retained by an incomplete row group.");

        if (_retainedValues is { } retainedValues)
        {
            _retainedValues = null;
            var combined = new T[checked(retainedValues.Length + values.Length)];
            retainedValues.CopyTo(combined, 0);
            values.CopyTo(combined.AsSpan(retainedValues.Length));
            SerializeValues(combined.AsSpan());
            return;
        }

        if (_column.Options.Repetition == ParquetRepetition.Repeated)
        {
            SerializeRepeated(values);
            return;
        }

        if (_column.Converter is { } converter)
        {
            SerializeConverted(values, converter);
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

    void SerializeConverted(ReadOnlySpan<T> values, ParquetValueConverter converter)
    {
        if (!converter.SupportsValueType(typeof(T)))
            throw new InvalidOperationException(
                $"Column '{_column.Name}' uses a converter for '{converter.ValueType}' and cannot serialize '{typeof(T)}'.");
        var nullable = converter.IsNullableValueType(typeof(T));
        var optional = _column.Options.Repetition == ParquetRepetition.Optional;
        if (nullable != optional)
            throw new InvalidOperationException(
                $"Column '{_column.Name}' is {(optional ? "optional" : "required")} and cannot serialize " +
                $"converter values of type '{typeof(T)}'.");
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            throw new NotSupportedException(
                $"Custom converter value type '{typeof(T)}' must be unmanaged.");

        var physicalType = converter.PhysicalType;
        if (physicalType == typeof(bool))
            SerializeConverted<T, bool>(values, converter);
        else if (physicalType == typeof(byte))
            SerializeConverted<T, byte>(values, converter);
        else if (physicalType == typeof(ushort))
            SerializeConverted<T, ushort>(values, converter);
        else if (physicalType == typeof(int))
            SerializeConverted<T, int>(values, converter);
        else if (physicalType == typeof(uint))
            SerializeConverted<T, uint>(values, converter);
        else if (physicalType == typeof(long))
            SerializeConverted<T, long>(values, converter);
        else if (physicalType == typeof(ulong))
            SerializeConverted<T, ulong>(values, converter);
        else if (physicalType == typeof(float))
            SerializeConverted<T, float>(values, converter);
        else if (physicalType == typeof(double))
            SerializeConverted<T, double>(values, converter);
        else if (physicalType == typeof(Guid))
            SerializeConverted<T, Guid>(values, converter);
        else if (physicalType == typeof(DateOnly))
            SerializeConverted<T, DateOnly>(values, converter);
        else if (physicalType == typeof(DateTime))
            SerializeConverted<T, DateTime>(values, converter);
        else if (physicalType == typeof(DateTimeOffset))
            SerializeConverted<T, DateTimeOffset>(values, converter);
        else if (physicalType == typeof(TimeOnly))
            SerializeConverted<T, TimeOnly>(values, converter);
        else
            throw new NotSupportedException(
                $"Converter physical CLR type '{physicalType}' is not supported for column '{_column.Name}'.");
    }

    void SerializeConverted<TValue, TPhysical>(ReadOnlySpan<TValue> values, ParquetValueConverter converter)
        where TPhysical : unmanaged
    {
        var nullable = converter.IsNullableValueType(typeof(TValue));
        var elementSize = nullable ? Unsafe.SizeOf<TPhysical?>() : Unsafe.SizeOf<TPhysical>();
        var rented = _owner.BufferWriters.BufferPool.Rent(
            checked((uint)values.Length * (uint)elementSize));
        try
        {
            converter.ConvertToPhysical(AsBytes(values), rented.Span, values.Length, nullable);
            if (nullable)
                SerializeConvertedPhysical(ParquetBuffer.AsReadOnlySpan<TPhysical?>(rented, values.Length));
            else
                SerializeConvertedPhysical(ParquetBuffer.AsReadOnlySpan<TPhysical>(rented, values.Length));
        }
        finally
        {
            rented.Dispose();
        }
    }

    void SerializeConvertedPhysical<TPhysical>(ReadOnlySpan<TPhysical> values)
    {
        if (typeof(TPhysical) == typeof(bool?))
            SerializeOptionalTyped(Reinterpret<TPhysical, bool?>(values));
        else if (typeof(TPhysical) == typeof(bool))
            SerializeTyped(Reinterpret<TPhysical, bool>(values));
        else if (typeof(TPhysical) == typeof(int?))
            SerializeOptionalTyped(Reinterpret<TPhysical, int?>(values));
        else if (typeof(TPhysical) == typeof(int))
            SerializeTyped(Reinterpret<TPhysical, int>(values));
        else if (typeof(TPhysical) == typeof(byte?))
            SerializeNullableByte(Reinterpret<TPhysical, byte?>(values));
        else if (typeof(TPhysical) == typeof(byte))
            SerializeByte(Reinterpret<TPhysical, byte>(values));
        else if (typeof(TPhysical) == typeof(ushort?))
            SerializeNullableUInt16(Reinterpret<TPhysical, ushort?>(values));
        else if (typeof(TPhysical) == typeof(ushort))
            SerializeUInt16(Reinterpret<TPhysical, ushort>(values));
        else if (typeof(TPhysical) == typeof(uint?))
            SerializeNullableUInt32(Reinterpret<TPhysical, uint?>(values));
        else if (typeof(TPhysical) == typeof(uint))
            SerializeUInt32(Reinterpret<TPhysical, uint>(values));
        else if (typeof(TPhysical) == typeof(long?))
            SerializeOptionalTyped(Reinterpret<TPhysical, long?>(values));
        else if (typeof(TPhysical) == typeof(long))
            SerializeTyped(Reinterpret<TPhysical, long>(values));
        else if (typeof(TPhysical) == typeof(ulong?))
            SerializeNullableUInt64(Reinterpret<TPhysical, ulong?>(values));
        else if (typeof(TPhysical) == typeof(ulong))
            SerializeUInt64(Reinterpret<TPhysical, ulong>(values));
        else if (typeof(TPhysical) == typeof(float?))
            SerializeOptionalTyped(Reinterpret<TPhysical, float?>(values));
        else if (typeof(TPhysical) == typeof(float))
            SerializeTyped(Reinterpret<TPhysical, float>(values));
        else if (typeof(TPhysical) == typeof(double?))
            SerializeOptionalTyped(Reinterpret<TPhysical, double?>(values));
        else if (typeof(TPhysical) == typeof(double))
            SerializeTyped(Reinterpret<TPhysical, double>(values));
        else if (typeof(TPhysical) == typeof(Guid?))
            SerializeNullableGuids(Reinterpret<TPhysical, Guid?>(values));
        else if (typeof(TPhysical) == typeof(Guid))
            SerializeGuids(Reinterpret<TPhysical, Guid>(values));
        else if (typeof(TPhysical) == typeof(DateOnly?))
            SerializeNullableDateOnly(Reinterpret<TPhysical, DateOnly?>(values));
        else if (typeof(TPhysical) == typeof(DateOnly))
            SerializeDateOnly(Reinterpret<TPhysical, DateOnly>(values));
        else if (typeof(TPhysical) == typeof(DateTime?))
            SerializeNullableDateTime(Reinterpret<TPhysical, DateTime?>(values));
        else if (typeof(TPhysical) == typeof(DateTime))
            SerializeDateTime(Reinterpret<TPhysical, DateTime>(values));
        else if (typeof(TPhysical) == typeof(DateTimeOffset?))
            SerializeNullableDateTimeOffset(Reinterpret<TPhysical, DateTimeOffset?>(values));
        else if (typeof(TPhysical) == typeof(DateTimeOffset))
            SerializeDateTimeOffset(Reinterpret<TPhysical, DateTimeOffset>(values));
        else if (typeof(TPhysical) == typeof(TimeOnly?))
            SerializeNullableTimeOnly(Reinterpret<TPhysical, TimeOnly?>(values));
        else if (typeof(TPhysical) == typeof(TimeOnly))
            SerializeTimeOnly(Reinterpret<TPhysical, TimeOnly>(values));
        else
            throw new NotSupportedException(
                $"Converter physical CLR type '{typeof(TPhysical)}' is not supported for column '{_column.Name}'.");
    }

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
        if (TrySerializeRequiredDateTimeDictionary(values, timestamp))
            return;

        var rented = _owner.BufferWriters.RentScratch<long>(checked((uint)values.Length));
        try
        {
            var converted = ParquetBuffer.AsSpan<long>(rented, values.Length);
            var expectedKind = timestamp.IsAdjustedToUtc ? DateTimeKind.Utc : DateTimeKind.Unspecified;
            switch (timestamp.Unit)
            {
                case TimeUnit.Millis:
                    for (var i = 0; i < values.Length; i++)
                    {
                        var value = values[i];
                        if (value.Kind != expectedKind)
                            throw new InvalidOperationException(
                                $"DateTime values must have kind '{expectedKind}', got '{value.Kind}'.");
                        converted[i] = TimestampConversion.DivideFloor(
                            value.Ticks - DateTime.UnixEpoch.Ticks, TimeSpan.TicksPerMillisecond);
                    }
                    break;
                case TimeUnit.Micros:
                    for (var i = 0; i < values.Length; i++)
                    {
                        var value = values[i];
                        if (value.Kind != expectedKind)
                            throw new InvalidOperationException(
                                $"DateTime values must have kind '{expectedKind}', got '{value.Kind}'.");
                        converted[i] = TimestampConversion.DivideFloor(
                            value.Ticks - DateTime.UnixEpoch.Ticks, 10);
                    }
                    break;
                case TimeUnit.Nanos:
                    for (var i = 0; i < values.Length; i++)
                    {
                        var value = values[i];
                        if (value.Kind != expectedKind)
                            throw new InvalidOperationException(
                                $"DateTime values must have kind '{expectedKind}', got '{value.Kind}'.");
                        converted[i] = checked((value.Ticks - DateTime.UnixEpoch.Ticks) * 100);
                    }
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(timestamp), timestamp.Unit,
                        "Time unit must be a defined TimeUnit value.");
            }
            SerializeTyped(converted);
        }
        finally
        {
            _owner.BufferWriters.ReturnScratch(rented);
        }
    }

    bool TrySerializeRequiredDateTimeDictionary(ReadOnlySpan<DateTime> values,
        LogicalType.Timestamp timestamp)
    {
        if (typeof(T) != typeof(DateTime) || _column.Converter is not null
            || _column.Options.Repetition != ParquetRepetition.Required
            || _owner.WritePageIndexes || _column.Options.BloomFilter is not null)
            return false;

        var columnOrdinal = _owner.GetColumnOrdinal(_leafColumn);
        var projection = _owner.ColumnProjectionInfosByOrdinal[columnOrdinal];
        if (projection.MaxDefinitionLevel != 0 || projection.MaxRepetitionLevel != 0)
            return false;
        var strategyContext = _owner.GetPageStrategyContext(columnOrdinal);
        if (strategyContext.Strategy.GetDictionaryMode() != DictionaryMode.Forced)
            return false;
        if (HasPendingData)
            throw new InvalidOperationException(
                "SerializedColumn already contains pending data. Call RowGroupWriter.Write(serialized) before Serialize(...) again.");

        var statistics = CreateRequiredDateTimeStatistics(values, timestamp);
        Pages.Clear();
        ColumnOrdinal = columnOrdinal;
        RowCount = checked((uint)values.Length);
        Statistics = statistics;
        HasPendingData = true;
        Plank.Writing.Encoding.Encoding.EncodeRequiredDateTimeDictionary(_owner.BufferWriters, _column, values,
            timestamp, strategyContext, Pages, GetOrCreateDictionaryState<DateTime>());
        _bloomFilterByteLength = 0;
        return true;
    }

    static ColumnStatistics CreateRequiredDateTimeStatistics(ReadOnlySpan<DateTime> values,
        LogicalType.Timestamp timestamp)
    {
        if (values.IsEmpty)
            return ColumnStatistics.Empty(0);

        var expectedKind = timestamp.IsAdjustedToUtc ? DateTimeKind.Utc : DateTimeKind.Unspecified;
        var first = values[0];
        if (first.Kind != expectedKind)
            throw new InvalidOperationException(
                $"DateTime values must have kind '{expectedKind}', got '{first.Kind}'.");
        var minTicks = first.Ticks;
        var maxTicks = minTicks;
        for (var i = 1; i < values.Length; i++)
        {
            var value = values[i];
            if (value.Kind != expectedKind)
                throw new InvalidOperationException(
                    $"DateTime values must have kind '{expectedKind}', got '{value.Kind}'.");
            var ticks = value.Ticks;
            if (ticks < minTicks)
                minTicks = ticks;
            if (ticks > maxTicks)
                maxTicks = ticks;
        }

        return ColumnStatistics.FromInt64(
            TimestampConversion.FromDateTimeTicks(minTicks, timestamp.Unit),
            TimestampConversion.FromDateTimeTicks(maxTicks, timestamp.Unit), 0);
    }

    void SerializeNullableDateTime(ReadOnlySpan<DateTime?> values)
    {
        var timestamp = RequireTimestampLogicalType(_column);
        var rented = _owner.BufferWriters.RentScratch<long>(checked((uint)values.Length));
        try
        {
            var converted = ParquetBuffer.AsSpan<long>(rented, values.Length);
            var presentCount = ConvertNullableDateTimes(values, converted, timestamp);
            SerializeOptionalConverted(values, converted[..presentCount], values.Length - presentCount);
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
        var rented = _owner.BufferWriters.RentScratch<long>(checked((uint)values.Length));
        try
        {
            var converted = ParquetBuffer.AsSpan<long>(rented, values.Length);
            var presentCount = ConvertNullableDateTimeOffsets(values, converted, timestamp.Unit);
            SerializeOptionalConverted(values, converted[..presentCount], values.Length - presentCount);
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

    void SerializeOptionalConverted<TSource, TValue>(ReadOnlySpan<TSource?> values,
        ReadOnlySpan<TValue> densePresentValues, long nullCount)
        where TSource : struct
        where TValue : struct
    {
        var columnOrdinal = _owner.GetColumnOrdinal(_leafColumn);
        var strategyContext = _owner.GetPageStrategyContext(columnOrdinal);
        ArgumentNullException.ThrowIfNull(strategyContext);
        if (HasPendingData)
            throw new InvalidOperationException(
                "SerializedColumn already contains pending data. Call RowGroupWriter.Write(serialized) before Serialize(...) again.");
        Pages.Clear();
        ColumnOrdinal = columnOrdinal;
        RowCount = checked((uint)values.Length);
        Statistics = ColumnStatistics.Create(_column, densePresentValues, nullCount);
        HasPendingData = true;

        Plank.Writing.Encoding.Encoding.EncodeOptionalConverted(_owner.BufferWriters, _column, values,
            densePresentValues, strategyContext, Pages, _owner.DataPageVersion,
            _owner.ColumnProjectionInfosByOrdinal[columnOrdinal], GetOrCreateDictionaryState<TValue>());
        _bloomFilterByteLength = BloomFilterBuilder.Build(_owner.BufferWriters, _column, densePresentValues,
            ref _bloomFilterBuffer);
        if (_owner.WritePageIndexes && !TryAssignSingleDataPageStatistics(Statistics))
            AssignOptionalDensePageStatistics(densePresentValues);
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

        var dictionaryState = GetOrCreateDictionaryState<TValue>();
        var hasDictionaryStatistics = Plank.Writing.Encoding.Encoding.Encode(_owner.BufferWriters, _column, values,
            strategyContext, Pages, _owner.DataPageVersion,
            _owner.ColumnProjectionInfosByOrdinal[columnOrdinal], dictionaryState);
        _bloomFilterByteLength = BloomFilterBuilder.Build(_owner.BufferWriters, _column, values,
            ref _bloomFilterBuffer);
        if (_owner.WritePageIndexes && TryAssignInt32ColumnAndPageStatistics(values))
            return;

        var statisticsValues = hasDictionaryStatistics && typeof(TValue) != typeof(float)
            && typeof(TValue) != typeof(double)
            ? dictionaryState.AsSpan()
            : values;
        Statistics = ColumnStatistics.CreateWithReusableBinaryBuffers(_column, statisticsValues, 0,
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

        if (typeof(TValue) == typeof(bool) || typeof(TValue) == typeof(int) || typeof(TValue) == typeof(long)
            || typeof(TValue) == typeof(float) || typeof(TValue) == typeof(double))
        {
            var firstPresentIndex = IndexOfFirstPresent(values);
            if (firstPresentIndex >= 0)
            {
                var remainingValues = values[firstPresentIndex..];
                var rented = _owner.BufferWriters.RentScratch<TValue>(checked((uint)remainingValues.Length));
                try
                {
                    var denseValues = ParquetBuffer.AsSpan<TValue>(rented, remainingValues.Length);
                    var presentCount = CompactPresentValues(remainingValues, denseValues);
                    SerializeOptionalConverted(values, denseValues[..presentCount], values.Length - presentCount);
                }
                finally
                {
                    _owner.BufferWriters.ReturnScratch(rented);
                }
                return;
            }
        }

        Pages.Clear();
        ColumnOrdinal = columnOrdinal;
        RowCount = checked((uint)values.Length);
        Statistics = ColumnStatistics.CreateOptionalWithReusableBinaryBuffers(_column, values,
            ref _statisticsMinValueBuffer, ref _statisticsMaxValueBuffer, _owner.BufferWriters.BufferPool);
        HasPendingData = true;

        Plank.Writing.Encoding.Encoding.EncodeOptional(_owner.BufferWriters, _column, values, strategyContext, Pages,
            _owner.DataPageVersion, _owner.ColumnProjectionInfosByOrdinal[columnOrdinal],
            GetOrCreateDictionaryState<TValue>());
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
            _owner.DataPageVersion, _owner.ColumnProjectionInfosByOrdinal[columnOrdinal],
            GetOrCreateDictionaryState<TValue>());
        _bloomFilterByteLength = BloomFilterBuilder.BuildOptionalReferences(_owner.BufferWriters, _column, values,
            ref _bloomFilterBuffer);
        if (_owner.WritePageIndexes && !TryAssignSingleDataPageStatistics(Statistics))
            AssignOptionalPageStatistics(values);
    }

    void PreparePages()
    {
        var columnOrdinal = checked((int)ColumnOrdinal);
        var compression = _owner.ColumnCompressionsByOrdinal[columnOrdinal];
        for (var i = 0; i < Pages.Count; i++)
        {
            ref var page = ref Pages[i];
            switch (page.Kind)
            {
                case PageKind.Dictionary:
                    PrepareDictionaryPage(ref page, compression);
                    break;
                case PageKind.DataV1:
                case PageKind.DataV2:
                    PrepareDataPage(ref page, compression);
                    break;
                default:
                    throw new InvalidOperationException($"Unknown page kind '{page.Kind}'.");
            }
        }
    }

    void PrepareDictionaryPage(ref Page page, ResolvedCompression compression)
    {
        var uncompressedContentSize = page.Content.WrittenLength;
        if (compression.Kind != CompressionKind.None && uncompressedContentSize > 0)
            CompressAndReplace(ref page.Content, compression);

        page.UncompressedContentSize = uncompressedContentSize;
        page.Header.Reset();
        ParquetMetadataThriftWriter.WriteDictionaryPageHeader(ref page.Header, page.DictionaryValueCount,
            uncompressedContentSize, page.Content.WrittenLength, GetPageCrc(ref page));
    }

    void PrepareDataPage(ref Page page, ResolvedCompression compression)
    {
        var uncompressedContentSize = page.Content.WrittenLength;
        var levelBytes = checked(page.RepetitionLevelsByteLength + page.DefinitionLevelsByteLength);
        if (levelBytes > (uint)uncompressedContentSize)
            throw new InvalidOperationException(
                $"Invalid level byte lengths ({levelBytes}) for data page content size {uncompressedContentSize}.");

        var compressed = _owner.DataPageVersion == ParquetDataPageVersion.V1
            ? PrepareDataPageV1(ref page, compression, uncompressedContentSize)
            : PrepareDataPageV2(ref page, compression, levelBytes);

        page.UncompressedContentSize = uncompressedContentSize;
        page.Header.Reset();
        if (_owner.DataPageVersion == ParquetDataPageVersion.V1)
        {
            ParquetMetadataThriftWriter.WriteDataPageHeaderV1(ref page.Header, page.ValueCount, page.Encoding,
                uncompressedContentSize, page.Content.WrittenLength, GetPageCrc(ref page));
            return;
        }

        ParquetMetadataThriftWriter.WriteDataPageHeaderV2(ref page.Header, page.RowCount, page.ValueCount,
            page.NullCount, page.RepetitionLevelsByteLength, page.DefinitionLevelsByteLength, page.Encoding,
            uncompressedContentSize, page.Content.WrittenLength, compressed, GetPageCrc(ref page));
    }

    bool PrepareDataPageV1(ref Page page, ResolvedCompression compression, int uncompressedContentSize)
    {
        if (compression.Kind == CompressionKind.None || uncompressedContentSize == 0)
            return false;

        CompressAndReplace(ref page.Content, compression);
        return true;
    }

    bool PrepareDataPageV2(ref Page page, ResolvedCompression compression, uint levelBytes)
    {
        var valueBytes = page.Content.WrittenLength - checked((int)levelBytes);
        if (compression.Kind == CompressionKind.None || valueBytes == 0)
            return false;

        if (levelBytes == 0)
        {
            CompressAndReplace(ref page.Content, compression);
            return true;
        }

        EnsurePageBuffer(ref _compressionInput);
        EnsurePageBuffer(ref _compressedValues);
        EnsurePageBuffer(ref _compressedContent);
        _compressionInput.Reset();
        _compressedContent.Reset();

        var source = _compressionContext.GetContiguousSourceSpan(ref page.Content);
        var levelBytesInt32 = checked((int)levelBytes);
        _compressedContent.Write(source[..levelBytesInt32]);
        _compressionInput.Write(source[levelBytesInt32..]);
        Plank.Writing.Compression.Compression.Compress(compression.Kind, compression.Level,
            _compressionContext, ref _compressionInput, ref _compressedValues);
        _compressedContent.CopyFrom(ref _compressedValues);
        Swap(ref page.Content, ref _compressedContent);
        return true;
    }

    void CompressAndReplace(ref BufferWriter content, ResolvedCompression compression)
    {
        EnsurePageBuffer(ref _compressedContent);
        Plank.Writing.Compression.Compression.Compress(compression.Kind, compression.Level,
            _compressionContext, ref content, ref _compressedContent);
        Swap(ref content, ref _compressedContent);
    }

    void EnsurePageBuffer(ref BufferWriter buffer)
    {
        if (!buffer.IsInitialized)
            buffer = _owner.BufferWriters.CreatePageBufferWriter();
    }

    uint? GetPageCrc(ref Page page)
        => _owner.WritePageCrc ? page.Content.ComputeCrc32() : null;

    static void Swap(ref BufferWriter left, ref BufferWriter right)
        => (left, right) = (right, left);

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
        _compressedContent.Dispose();
        _compressionInput.Dispose();
        _compressedValues.Dispose();
        _compressionContext.Dispose();
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

    void AssignOptionalDensePageStatistics<TValue>(ReadOnlySpan<TValue> values)
        where TValue : struct
    {
        var valueOffset = 0;
        for (var i = 0; i < Pages.Count; i++)
        {
            ref var page = ref Pages[i];
            if (page.Kind != PageKind.DataV2)
                continue;
            var pageValueCount = checked((int)(page.RowCount - page.NullCount));
            var pageValues = values.Slice(valueOffset, pageValueCount);
            page.Statistics = ColumnStatistics.Create(_column, pageValues, page.NullCount);
            valueOffset += pageValueCount;
        }

        if (valueOffset != values.Length)
            throw new InvalidOperationException(
                $"Optional page statistics covered {valueOffset} values, but the column contains {values.Length} present values.");
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

    static ReadOnlySpan<byte> AsBytes<TValue>(ReadOnlySpan<TValue> values)
    {
        if (values.IsEmpty)
            return [];
        ref var first = ref Unsafe.As<TValue, byte>(ref MemoryMarshal.GetReference(values));
        return MemoryMarshal.CreateReadOnlySpan(ref first, checked(values.Length * Unsafe.SizeOf<TValue>()));
    }

    static ReadOnlySpan<TTo> Reinterpret<TFrom, TTo>(ReadOnlySpan<TFrom> values)
    {
        if (Unsafe.SizeOf<TFrom>() != Unsafe.SizeOf<TTo>())
            throw new InvalidOperationException(
                $"Cannot reinterpret converter values from '{typeof(TFrom)}' to '{typeof(TTo)}'.");
        if (values.IsEmpty)
            return [];
        ref var first = ref Unsafe.As<TFrom, TTo>(ref MemoryMarshal.GetReference(values));
        return MemoryMarshal.CreateReadOnlySpan(ref first, values.Length);
    }

    static long ToUnixTime(DateTimeOffset value, TimeUnit unit)
        => ToUnixTimeFromTicks(value.UtcDateTime.Ticks, unit);

    static int ConvertNullableDateTimes(ReadOnlySpan<DateTime?> values, Span<long> destination,
        LogicalType.Timestamp timestamp)
    {
        var expectedKind = timestamp.IsAdjustedToUtc ? DateTimeKind.Utc : DateTimeKind.Unspecified;
        return timestamp.Unit switch
        {
            TimeUnit.Millis => ConvertNullableDateTimesDivided(values, destination, expectedKind,
                TimeSpan.TicksPerMillisecond),
            TimeUnit.Micros => ConvertNullableDateTimesDivided(values, destination, expectedKind, 10),
            TimeUnit.Nanos => ConvertNullableDateTimesNanos(values, destination, expectedKind),
            _ => throw new ArgumentOutOfRangeException(nameof(timestamp), timestamp.Unit,
                "Time unit must be a defined TimeUnit value.")
        };
    }

    static int CompactPresentValues<TValue>(ReadOnlySpan<TValue?> values, Span<TValue> destination)
        where TValue : struct
    {
        var count = 0;
        for (var i = 0; i < values.Length; i++)
            if (values[i] is { } value)
                destination[count++] = value;
        return count;
    }

    static int IndexOfFirstPresent<TValue>(ReadOnlySpan<TValue?> values)
        where TValue : struct
    {
        for (var i = 0; i < values.Length; i++)
            if (values[i].HasValue)
                return i;
        return -1;
    }

    static int ConvertNullableDateTimesDivided(ReadOnlySpan<DateTime?> values, Span<long> destination,
        DateTimeKind expectedKind, long divisor)
    {
        var count = 0;
        for (var i = 0; i < values.Length; i++)
        {
            if (values[i] is not { } value)
                continue;
            if (value.Kind != expectedKind)
                throw new InvalidOperationException(
                    $"DateTime values must have kind '{expectedKind}', got '{value.Kind}'.");

            destination[count++] = TimestampConversion.DivideFloor(value.Ticks - DateTime.UnixEpoch.Ticks, divisor);
        }

        return count;
    }

    static int ConvertNullableDateTimesNanos(ReadOnlySpan<DateTime?> values, Span<long> destination,
        DateTimeKind expectedKind)
    {
        var count = 0;
        for (var i = 0; i < values.Length; i++)
        {
            if (values[i] is not { } value)
                continue;
            if (value.Kind != expectedKind)
                throw new InvalidOperationException(
                    $"DateTime values must have kind '{expectedKind}', got '{value.Kind}'.");

            destination[count++] = checked((value.Ticks - DateTime.UnixEpoch.Ticks) * 100);
        }

        return count;
    }

    static int ConvertNullableDateTimeOffsets(ReadOnlySpan<DateTimeOffset?> values, Span<long> destination,
        TimeUnit unit)
        => unit switch
        {
            TimeUnit.Millis => ConvertNullableDateTimeOffsetsDivided(values, destination,
                TimeSpan.TicksPerMillisecond),
            TimeUnit.Micros => ConvertNullableDateTimeOffsetsDivided(values, destination, 10),
            TimeUnit.Nanos => ConvertNullableDateTimeOffsetsNanos(values, destination),
            _ => throw new ArgumentOutOfRangeException(nameof(unit), unit, "Time unit must be a defined TimeUnit value.")
        };

    static int ConvertNullableDateTimeOffsetsDivided(ReadOnlySpan<DateTimeOffset?> values, Span<long> destination,
        long divisor)
    {
        var count = 0;
        for (var i = 0; i < values.Length; i++)
            if (values[i] is { } value)
                destination[count++] = TimestampConversion.DivideFloor(
                    value.UtcDateTime.Ticks - DateTime.UnixEpoch.Ticks, divisor);
        return count;
    }

    static int ConvertNullableDateTimeOffsetsNanos(ReadOnlySpan<DateTimeOffset?> values, Span<long> destination)
    {
        var count = 0;
        for (var i = 0; i < values.Length; i++)
            if (values[i] is { } value)
                destination[count++] = checked((value.UtcDateTime.Ticks - DateTime.UnixEpoch.Ticks) * 100);
        return count;
    }

    static long ToUnixTimeFromTicks(long ticks, TimeUnit unit)
        => TimestampConversion.FromDateTimeTicks(ticks, unit);

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
