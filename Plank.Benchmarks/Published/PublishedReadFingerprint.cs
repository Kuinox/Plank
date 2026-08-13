using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;

namespace Plank.Benchmarks.Published;

static class PublishedReadFingerprint
{
    const ulong Offset = 14695981039346656037UL;
    const ulong Prime = 1099511628211UL;

    public static PublishedReadResult Expected(PublishedBenchmarkDataSet dataSet)
    {
        var aggregate = Start();
        long valueCount = 0;
        for (var rowGroupIndex = 0; rowGroupIndex < dataSet.RowGroupCount; rowGroupIndex++)
            for (var columnIndex = 0; columnIndex < dataSet.Columns.Count; columnIndex++)
            {
                var column = dataSet.Columns[columnIndex];
                var values = column.Values[rowGroupIndex];
                var fingerprint = StartPiece(columnIndex, rowGroupIndex, values.Length);
                fingerprint = AddExpectedValues(fingerprint, column, rowGroupIndex);
                var piece = new PublishedReadResult(values.Length, fingerprint);
                aggregate = Combine(aggregate, piece);
                valueCount = checked(valueCount + values.Length);
            }
        return new PublishedReadResult(valueCount, aggregate);
    }

    public static ulong Start()
        => Offset;

    public static ulong StartPiece(int columnIndex, int rowGroupIndex, int valueCount)
    {
        var hash = AddUInt64(Offset, unchecked((uint)columnIndex));
        hash = AddUInt64(hash, unchecked((uint)rowGroupIndex));
        return AddUInt64(hash, unchecked((uint)valueCount));
    }

    public static ulong Combine(ulong aggregate, PublishedReadResult piece)
    {
        aggregate = AddUInt64(aggregate, unchecked((ulong)piece.ValueCount));
        return AddUInt64(aggregate, piece.Fingerprint);
    }

    static ulong AddExpectedValues(ulong hash, PublishedBenchmarkDataSet.Column column, int rowGroupIndex)
        => (column.Kind, column.Nullable) switch
        {
            (BenchmarkColumnKind.Boolean, false) => AddValues(hash, (bool[])column.Values[rowGroupIndex]),
            (BenchmarkColumnKind.Boolean, true) => AddValues(hash, (bool?[])column.Values[rowGroupIndex]),
            (BenchmarkColumnKind.Int32, false) => AddValues(hash, (int[])column.Values[rowGroupIndex]),
            (BenchmarkColumnKind.Int32, true) => AddValues(hash, (int?[])column.Values[rowGroupIndex]),
            (BenchmarkColumnKind.Int64, false) => AddValues(hash, (long[])column.Values[rowGroupIndex]),
            (BenchmarkColumnKind.Int64, true) => AddValues(hash, (long?[])column.Values[rowGroupIndex]),
            (BenchmarkColumnKind.Double, false) => AddValues(hash, (double[])column.Values[rowGroupIndex]),
            (BenchmarkColumnKind.Double, true) => AddValues(hash, (double?[])column.Values[rowGroupIndex]),
            (BenchmarkColumnKind.Timestamp, false) => AddValues(hash, (DateTime[])column.Values[rowGroupIndex]),
            (BenchmarkColumnKind.Timestamp, true) => AddValues(hash, (DateTime?[])column.Values[rowGroupIndex]),
            (BenchmarkColumnKind.String, _) => AddBinaryValues(hash,
                (byte[]?[])(column.Utf8Values
                    ?? throw new InvalidOperationException($"Column '{column.Name}' has no UTF-8 values."))[
                        rowGroupIndex]),
            _ => throw new NotSupportedException($"Unsupported column kind '{column.Kind}'.")
        };

    static ulong AddValues<T>(ulong hash, ReadOnlySpan<T> values)
    {
        for (var valueIndex = 0; valueIndex < values.Length; valueIndex++)
            hash = AddValue(hash, values[valueIndex]);
        return hash;
    }

    static ulong AddBinaryValues(ulong hash, ReadOnlySpan<byte[]?> values)
    {
        for (var valueIndex = 0; valueIndex < values.Length; valueIndex++)
            hash = values[valueIndex] is { } value ? AddBytes(hash, value) : AddNull(hash);
        return hash;
    }

    public static ulong AddValue(ulong hash, object? value)
        => value switch
        {
            null => AddNull(hash),
            bool boolean => AddValue(hash, boolean),
            int integer => AddValue(hash, integer),
            long integer => AddValue(hash, integer),
            double number => AddValue(hash, number),
            DateTime timestamp => AddValue(hash, timestamp),
            DateTimeOffset timestamp => AddValue(hash, timestamp),
            string text => AddString(hash, text),
            byte[] bytes => AddBytes(hash, bytes),
            _ => throw new NotSupportedException($"Unsupported fingerprint value '{value.GetType()}'.")
        };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong AddValue<T>(ulong hash, T value)
    {
        if (typeof(T) == typeof(bool))
            return AddValue(hash, Unsafe.As<T, bool>(ref value));
        if (typeof(T) == typeof(bool?))
            return AddValue(hash, Unsafe.As<T, bool?>(ref value));
        if (typeof(T) == typeof(int))
            return AddValue(hash, Unsafe.As<T, int>(ref value));
        if (typeof(T) == typeof(int?))
            return AddValue(hash, Unsafe.As<T, int?>(ref value));
        if (typeof(T) == typeof(long))
            return AddValue(hash, Unsafe.As<T, long>(ref value));
        if (typeof(T) == typeof(long?))
            return AddValue(hash, Unsafe.As<T, long?>(ref value));
        if (typeof(T) == typeof(double))
            return AddValue(hash, Unsafe.As<T, double>(ref value));
        if (typeof(T) == typeof(double?))
            return AddValue(hash, Unsafe.As<T, double?>(ref value));
        if (typeof(T) == typeof(DateTime))
            return AddValue(hash, Unsafe.As<T, DateTime>(ref value));
        if (typeof(T) == typeof(DateTime?))
            return AddValue(hash, Unsafe.As<T, DateTime?>(ref value));
        throw new NotSupportedException($"Unsupported fingerprint value '{typeof(T)}'.");
    }

    public static ulong AddNull(ulong hash)
        => AddUInt64(hash, 0);

    static ulong AddPresent(ulong hash)
        => AddUInt64(hash, 1);

    public static ulong AddValue(ulong hash, bool value)
        => AddUInt64(AddPresent(hash), value ? 1UL : 0UL);

    public static ulong AddValue(ulong hash, bool? value)
        => value.HasValue ? AddValue(hash, value.Value) : AddNull(hash);

    public static ulong AddValue(ulong hash, int value)
        => AddUInt64(AddPresent(hash), unchecked((uint)value));

    public static ulong AddValue(ulong hash, int? value)
        => value.HasValue ? AddValue(hash, value.Value) : AddNull(hash);

    public static ulong AddValue(ulong hash, long value)
        => AddUInt64(AddPresent(hash), unchecked((ulong)value));

    public static ulong AddValue(ulong hash, long? value)
        => value.HasValue ? AddValue(hash, value.Value) : AddNull(hash);

    public static ulong AddValue(ulong hash, double value)
        => AddUInt64(AddPresent(hash), unchecked((ulong)BitConverter.DoubleToInt64Bits(value)));

    public static ulong AddValue(ulong hash, double? value)
        => value.HasValue ? AddValue(hash, value.Value) : AddNull(hash);

    public static ulong AddValue(ulong hash, DateTime value)
        => AddUInt64(AddPresent(hash),
            unchecked((ulong)new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)).UtcTicks));

    public static ulong AddValue(ulong hash, DateTime? value)
        => value.HasValue ? AddValue(hash, value.Value) : AddNull(hash);

    public static ulong AddValue(ulong hash, DateTimeOffset value)
        => AddUInt64(AddPresent(hash), unchecked((ulong)value.UtcTicks));

    public static ulong AddValue(ulong hash, DateTimeOffset? value)
        => value.HasValue ? AddValue(hash, value.Value) : AddNull(hash);

    public static ulong AddString(ulong hash, string value)
    {
        var maximumByteCount = Encoding.UTF8.GetMaxByteCount(value.Length);
        byte[]? rented = null;
        Span<byte> bytes = maximumByteCount <= 256
            ? stackalloc byte[maximumByteCount]
            : (rented = ArrayPool<byte>.Shared.Rent(maximumByteCount));
        try
        {
            var byteCount = Encoding.UTF8.GetBytes(value, bytes);
            return AddBytes(hash, bytes[..byteCount]);
        }
        finally
        {
            if (rented is not null)
                ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public static ulong AddBytes(ulong hash, ReadOnlySpan<byte> value)
    {
        hash = AddUInt64(AddPresent(hash), unchecked((uint)value.Length));
        for (var index = 0; index < value.Length; index++)
        {
            hash ^= value[index];
            hash *= Prime;
        }
        return hash;
    }

    static ulong AddUInt64(ulong hash, ulong value)
    {
        for (var shift = 0; shift < 64; shift += 8)
        {
            hash ^= (byte)(value >> shift);
            hash *= Prime;
        }
        return hash;
    }
}
