using System.Buffers.Binary;
using System.Numerics;
using Plank.Schema;
using TextEncoding = System.Text.Encoding;

namespace Plank.Writing;

// One comparison per incoming row: the first crosses the append boundary, the rest
// compare adjacent incoming rows. Keep the keys separate until all columns arrive,
// since schema order need not match lexicographic sort-key order.
sealed class AppendSortingOrder(ParquetSortingColumn[] columns)
{
    readonly sbyte[]?[] _comparisons = new sbyte[]?[columns.Length];
    const sbyte Unknown = 2;

    internal sbyte[]? CompareIncoming<T>(Column column, int ordinal, T[] retained, ReadOnlySpan<T> incoming)
    {
        var key = FindKey(ordinal);
        if (key < 0)
            return null;
        var result = new sbyte[incoming.Length];
        if (retained.Length == 0)
        {
            Array.Fill(result, Unknown);
            return result;
        }
        object? previous = retained[^1];
        for (var i = 0; i < incoming.Length; i++)
        {
            object? current = incoming[i];
            result[i] = Compare(column, columns[key], previous, current);
            previous = current;
        }
        return result;
    }

    internal void Record(int ordinal, sbyte[]? comparisons)
    {
        var key = FindKey(ordinal);
        if (key >= 0)
            _comparisons[key] = comparisons;
    }

    internal ReadOnlySpan<ParquetSortingColumn> GetVerifiedColumns()
    {
        if (columns.Length == 0 || _comparisons[0] is not { } first)
            return [];
        foreach (var comparisons in _comparisons)
            if (comparisons is null || comparisons.Length != first.Length)
                return [];
        for (var row = 0; row < first.Length; row++)
            for (var key = 0; key < columns.Length; key++)
            {
                var comparison = _comparisons[key]![row];
                if (comparison > 0) // Out of order, or the order cannot be established.
                    return [];
                if (comparison < 0)
                    break;
            }
        return columns;
    }

    int FindKey(int ordinal)
    {
        for (var i = 0; i < columns.Length; i++)
            if (columns[i].ColumnOrdinal == ordinal)
                return i;
        return -1;
    }

    static sbyte Compare(Column column, ParquetSortingColumn sort, object? left, object? right)
    {
        // User converters can change order. Undefined Parquet orders cannot be verified.
        if (column.Converter is not null || column.PhysicalType == ParquetPhysicalType.Int96 ||
            column.LogicalType is LogicalType.Interval or LogicalType.Unknown or LogicalType.Variant
                or LogicalType.Geometry or LogicalType.Geography)
            return Unknown;
        // Null placement is independent of ascending/descending direction.
        if (left is null || right is null)
            return left is null && right is null ? (sbyte)0
                : (sbyte)((left is null) == sort.NullsFirst ? -1 : 1);
        var comparison = CompareNonNull(column, left, right);
        return comparison is { } value ? (sbyte)(Math.Sign(value) * (sort.Descending ? -1 : 1)) : Unknown;
    }

    static int? CompareNonNull(Column column, object left, object right)
    {
        var unsigned = column.LogicalType is LogicalType.Int { IsSigned: false };
        return (left, right) switch
        {
            (bool a, bool b) => a.CompareTo(b),
            (byte a, byte b) => a.CompareTo(b),
            (sbyte a, sbyte b) => a.CompareTo(b),
            (short a, short b) => a.CompareTo(b),
            (ushort a, ushort b) => a.CompareTo(b),
            (int a, int b) => unsigned ? unchecked((uint)a).CompareTo(unchecked((uint)b)) : a.CompareTo(b),
            (uint a, uint b) => unsigned ? a.CompareTo(b) : unchecked((int)a).CompareTo(unchecked((int)b)),
            (long a, long b) => unsigned ? unchecked((ulong)a).CompareTo(unchecked((ulong)b)) : a.CompareTo(b),
            (ulong a, ulong b) => unsigned ? a.CompareTo(b) : unchecked((long)a).CompareTo(unchecked((long)b)),
            (float a, float b) => CompareFloating(a, b),
            (double a, double b) => CompareFloating(a, b),
            (decimal a, decimal b) => a.CompareTo(b),
            (DateOnly a, DateOnly b) => a.CompareTo(b),
            (TimeOnly a, TimeOnly b) when column.LogicalType is LogicalType.Time time
                => TimeValue(a, time.Unit).CompareTo(TimeValue(b, time.Unit)),
            (DateTime a, DateTime b) when column.LogicalType is LogicalType.Timestamp timestamp
                => TimestampConversion.FromDateTimeTicks(a.Ticks, timestamp.Unit)
                    .CompareTo(TimestampConversion.FromDateTimeTicks(b.Ticks, timestamp.Unit)),
            (DateTimeOffset a, DateTimeOffset b) when column.LogicalType is LogicalType.Timestamp timestamp
                => TimestampConversion.FromDateTimeTicks(a.UtcTicks, timestamp.Unit)
                    .CompareTo(TimestampConversion.FromDateTimeTicks(b.UtcTicks, timestamp.Unit)),
            (string a, string b) => CompareBinary(column, TextEncoding.UTF8.GetBytes(a), TextEncoding.UTF8.GetBytes(b)),
            (byte[] a, byte[] b) => CompareBinary(column, a, b),
            (ReadOnlyMemory<byte> a, ReadOnlyMemory<byte> b) => CompareBinary(column, a.Span, b.Span),
            (Guid a, Guid b) => CompareBinary(column, a.ToByteArray(bigEndian: true), b.ToByteArray(bigEndian: true)),
            _ => null
        };
    }

    static long TimeValue(TimeOnly value, TimeUnit unit) => unit switch
    {
        TimeUnit.Millis => value.Ticks / TimeSpan.TicksPerMillisecond,
        TimeUnit.Micros => value.Ticks / 10,
        TimeUnit.Nanos => value.Ticks * 100,
        _ => throw new ArgumentOutOfRangeException(nameof(unit))
    };

    static int? CompareFloating(double left, double right)
        => double.IsNaN(left) || double.IsNaN(right) ||
            (left == 0 && right == 0 && double.IsNegative(left) != double.IsNegative(right))
            ? null : left.CompareTo(right);

    static int? CompareBinary(Column column, ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        if (column.LogicalType is LogicalType.Decimal)
            return new BigInteger(left, isUnsigned: false, isBigEndian: true)
                .CompareTo(new BigInteger(right, isUnsigned: false, isBigEndian: true));
        if (column.LogicalType is LogicalType.Float16)
            return left.Length == 2 && right.Length == 2
                ? CompareFloating((double)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(left)),
                    (double)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(right)))
                : null;
        return left.SequenceCompareTo(right);
    }
}
