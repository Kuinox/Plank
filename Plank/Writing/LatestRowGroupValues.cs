using Plank.Reading.Logical;
using Plank.Schema;
using TextEncoding = System.Text.Encoding;

namespace Plank.Writing;

sealed class LatestRowGroupValues
{
    static readonly DateOnly UnixEpochDate = new(1970, 1, 1);

    readonly Column[] _columns;
    readonly Array[] _values;

    LatestRowGroupValues(Column[] columns, Array[] values)
    {
        _columns = columns;
        _values = values;
    }

    internal static LatestRowGroupValues Read(RowGroup rowGroup, Column[] columns)
    {
        if (rowGroup.RowCount > int.MaxValue)
            throw new NotSupportedException("Appending to a row group with more than Int32.MaxValue rows is not supported.");

        var values = new Array[columns.Length];
        for (var i = 0; i < columns.Length; i++)
        {
            var column = columns[i];
            if (column.Options.Repetition == ParquetRepetition.Repeated)
                throw new NotSupportedException(
                    $"Appending to the latest row group is not supported for repeated column '{column.Name}'.");

            var optional = column.Options.Repetition == ParquetRepetition.Optional;
            values[i] = column.PhysicalType switch
            {
                ParquetPhysicalType.Boolean => optional
                    ? ReadFixed<bool?>(rowGroup, i)
                    : ReadFixed<bool>(rowGroup, i),
                ParquetPhysicalType.Int32 => optional
                    ? ReadFixed<int?>(rowGroup, i)
                    : ReadFixed<int>(rowGroup, i),
                ParquetPhysicalType.Int64 => optional
                    ? ReadFixed<long?>(rowGroup, i)
                    : ReadFixed<long>(rowGroup, i),
                ParquetPhysicalType.Float => optional
                    ? ReadFixed<float?>(rowGroup, i)
                    : ReadFixed<float>(rowGroup, i),
                ParquetPhysicalType.Double => optional
                    ? ReadFixed<double?>(rowGroup, i)
                    : ReadFixed<double>(rowGroup, i),
                ParquetPhysicalType.ByteArray or ParquetPhysicalType.FixedLenByteArray or ParquetPhysicalType.Int96
                    => ReadBinary(rowGroup, i),
                _ => throw new NotSupportedException(
                    $"Appending to the latest row group is not supported for physical type '{column.PhysicalType}'.")
            };
        }

        return new LatestRowGroupValues(columns, values);
    }

    internal T[] GetValues<T>(int columnOrdinal)
    {
        var source = _values[columnOrdinal];
        if (source is T[] exact)
            return exact;

        var result = new T[source.Length];
        var column = _columns[columnOrdinal];
        for (var i = 0; i < result.Length; i++)
            result[i] = ConvertValue<T>(source.GetValue(i), column);
        return result;
    }

    static T[] ReadFixed<T>(RowGroup rowGroup, int columnOrdinal)
    {
        var result = new T[checked((int)rowGroup.RowCount)];
        var offset = 0;
        foreach (var buffer in rowGroup.Column<T>(columnOrdinal))
        {
            buffer.Values.CopyTo(result.AsSpan(offset));
            offset = checked(offset + buffer.Count);
        }

        if (offset != result.Length)
            throw new InvalidDataException(
                $"Column {columnOrdinal} ended after {offset} values; expected {result.Length}.");
        return result;
    }

    static byte[][] ReadBinary(RowGroup rowGroup, int columnOrdinal)
    {
        var result = new byte[checked((int)rowGroup.RowCount)][];
        var offset = 0;
        foreach (var buffer in rowGroup.Column<byte>(columnOrdinal))
            for (var i = 0; i < buffer.Count; i++)
                result[offset++] = buffer.IsNull(i) ? null! : buffer.GetValue(i).ToArray();

        if (offset != result.Length)
            throw new InvalidDataException(
                $"Column {columnOrdinal} ended after {offset} values; expected {result.Length}.");
        return result;
    }

    static T ConvertValue<T>(object? value, Column column)
    {
        if (value is null)
            return default!;
        if (value is T typed)
            return typed;

        object converted = value switch
        {
            int number => ConvertInt32<T>(number, column),
            long number => ConvertInt64<T>(number, column),
            byte[] bytes => ConvertBinary<T>(bytes),
            _ => throw Unsupported<T>(column)
        };
        return (T)converted;
    }

    static object ConvertInt32<T>(int value, Column column)
    {
        var target = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        if (target == typeof(byte))
            return checked((byte)value);
        if (target == typeof(ushort))
            return checked((ushort)value);
        if (target == typeof(uint))
            return unchecked((uint)value);
        if (target == typeof(DateOnly))
            return DateOnly.FromDayNumber(checked(UnixEpochDate.DayNumber + value));
        if (target == typeof(TimeOnly))
            return DecodeTime(value, column.LogicalType);
        throw Unsupported<T>(column);
    }

    static object ConvertInt64<T>(long value, Column column)
    {
        var target = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        if (target == typeof(ulong))
            return unchecked((ulong)value);
        if (target == typeof(TimeOnly))
            return DecodeTime(value, column.LogicalType);
        if (target == typeof(DateTimeOffset))
            return DecodeTimestamp(value, column.LogicalType);
        if (target == typeof(DateTime))
        {
            var timestamp = DecodeTimestampValue(value, column.LogicalType).UtcDateTime;
            return column.LogicalType is LogicalType.Timestamp { IsAdjustedToUtc: false }
                ? DateTime.SpecifyKind(timestamp, DateTimeKind.Unspecified)
                : timestamp;
        }
        throw Unsupported<T>(column);
    }

    static object ConvertBinary<T>(byte[] value)
    {
        var target = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        if (target == typeof(ReadOnlyMemory<byte>))
            return new ReadOnlyMemory<byte>(value);
        if (target == typeof(string))
            return TextEncoding.UTF8.GetString(value);
        if (target == typeof(Guid))
            return new Guid(value, bigEndian: true);
        throw new NotSupportedException($"Cannot append binary values as '{typeof(T)}'.");
    }

    static TimeOnly DecodeTime(long value, LogicalType? logicalType)
        => logicalType switch
        {
            LogicalType.Time { Unit: TimeUnit.Millis } => new TimeOnly(checked(value * TimeSpan.TicksPerMillisecond)),
            LogicalType.Time { Unit: TimeUnit.Micros } => new TimeOnly(checked(value * 10)),
            LogicalType.Time { Unit: TimeUnit.Nanos } => new TimeOnly(value / 100),
            _ => throw new NotSupportedException("TimeOnly append requires a time logical type.")
        };

    static DateTimeOffset DecodeTimestamp(long value, LogicalType? logicalType)
    {
        if (logicalType is LogicalType.Timestamp { IsAdjustedToUtc: false })
            throw new NotSupportedException(
                "DateTimeOffset append is not supported for timestamps with local semantics.");
        return DecodeTimestampValue(value, logicalType);
    }

    static DateTimeOffset DecodeTimestampValue(long value, LogicalType? logicalType)
        => logicalType switch
        {
            LogicalType.Timestamp { Unit: TimeUnit.Millis } => DateTimeOffset.FromUnixTimeMilliseconds(value),
            LogicalType.Timestamp { Unit: TimeUnit.Micros } => DateTimeOffset.UnixEpoch.AddTicks(checked(value * 10)),
            LogicalType.Timestamp { Unit: TimeUnit.Nanos } => DateTimeOffset.UnixEpoch.AddTicks(value / 100),
            _ => throw new NotSupportedException("Date/time append requires a timestamp logical type.")
        };

    static NotSupportedException Unsupported<T>(Column column)
        => new($"Column '{column.Name}' cannot append retained values as '{typeof(T)}'.");
}
