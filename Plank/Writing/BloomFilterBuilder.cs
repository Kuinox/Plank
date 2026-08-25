using System.Runtime.CompilerServices;
using Plank.BloomFilters;
using Plank.Schema;

namespace Plank.Writing;

static class BloomFilterBuilder
{
    internal static int Build<T>(BufferWriterFactory buffers, Column column, ReadOnlySpan<T> values,
        ref ParquetBuffer buffer)
        where T : notnull
    {
        var options = column.Options.BloomFilter;
        if (options is null)
            return 0;

        if (column.Options.Repetition == ParquetRepetition.Repeated)
        {
            var valueCount = options.ExpectedDistinctValueCount ?? CountRepeatedValues(column, values);
            var bitset = Initialize(buffers, options, valueCount, ref buffer, out var byteLength);
            for (var i = 0; i < values.Length; i++)
                InsertRepeatedValue(column, values[i], bitset);
            return byteLength;
        }

        var count = options.ExpectedDistinctValueCount ?? checked((uint)values.Length);
        var destination = Initialize(buffers, options, count, ref buffer, out var length);
        InsertValues(column, values, destination);
        return length;
    }

    internal static int BuildOptional<T>(BufferWriterFactory buffers, Column column, ReadOnlySpan<T?> values,
        ref ParquetBuffer buffer)
        where T : struct
    {
        var options = column.Options.BloomFilter;
        if (options is null)
            return 0;

        var valueCount = options.ExpectedDistinctValueCount ?? CountPresent(values);
        var destination = Initialize(buffers, options, valueCount, ref buffer, out var length);
        InsertOptionalValues(column, values, destination);
        return length;
    }

    internal static int BuildOptionalReferences<T>(BufferWriterFactory buffers, Column column, ReadOnlySpan<T> values,
        ref ParquetBuffer buffer)
        where T : class
    {
        var options = column.Options.BloomFilter;
        if (options is null)
            return 0;

        var valueCount = options.ExpectedDistinctValueCount ?? CountPresent(values);
        var destination = Initialize(buffers, options, valueCount, ref buffer, out var length);
        InsertValues(column, values, destination);
        return length;
    }

    static Span<byte> Initialize(BufferWriterFactory buffers, ParquetBloomFilterOptions options, uint valueCount,
        ref ParquetBuffer buffer, out int byteLength)
    {
        byteLength = checked((int)options.GetBitsetSize(valueCount));
        if (buffer.IsEmpty || buffer.Length < byteLength)
        {
            buffer.Dispose();
            buffer = buffers.RentScratch(checked((uint)byteLength));
        }

        var bitset = buffer.Span[..byteLength];
        bitset.Clear();
        return bitset;
    }

    static uint CountPresent<T>(ReadOnlySpan<T?> values)
        where T : struct
    {
        var count = 0U;
        for (var i = 0; i < values.Length; i++)
            if (values[i].HasValue)
                count = checked(count + 1);
        return count;
    }

    static uint CountPresent<T>(ReadOnlySpan<T> values)
        where T : class
    {
        var count = 0U;
        for (var i = 0; i < values.Length; i++)
            if (values[i] is not null)
                count = checked(count + 1);
        return count;
    }

    static uint CountRepeatedValues<T>(Column column, ReadOnlySpan<T> values)
    {
        var count = 0U;
        for (var i = 0; i < values.Length; i++)
            count = checked(count + CountRepeatedValue(column, values[i]));
        return count;
    }

    static uint CountRepeatedValue(Column column, object? value)
    {
        if (value is null)
            return 0;
        if (IsBinaryLeaf(column, value))
            return 1;

        switch (value)
        {
            case int[] values:
                return checked((uint)values.Length);
            case int?[] values:
                return CountPresent(values);
            case long[] values:
                return checked((uint)values.Length);
            case long?[] values:
                return CountPresent(values);
            case float[] values:
                return checked((uint)values.Length);
            case float?[] values:
                return CountPresent(values);
            case double[] values:
                return checked((uint)values.Length);
            case double?[] values:
                return CountPresent(values);
            case byte[][] values:
                return CountPresent(values);
            case ReadOnlyMemory<byte>[] values:
                return checked((uint)values.Length);
            case ReadOnlyMemory<byte>?[] values:
                return CountPresent(values);
            case not Array:
                return 1;
        }

        var array = (Array)value;
        var count = 0U;
        for (var i = 0; i < array.Length; i++)
            count = checked(count + CountRepeatedValue(column, array.GetValue(i)));
        return count;
    }

    static void InsertRepeatedValue(Column column, object? value, Span<byte> bitset)
    {
        if (value is null)
            return;
        if (IsBinaryLeaf(column, value))
        {
            InsertObject(column, value, bitset);
            return;
        }

        switch (value)
        {
            case int[] values:
                InsertValues(column, values, bitset);
                return;
            case int?[] values:
                InsertOptionalValues(column, values, bitset);
                return;
            case long[] values:
                InsertValues(column, values, bitset);
                return;
            case long?[] values:
                InsertOptionalValues(column, values, bitset);
                return;
            case float[] values:
                InsertValues(column, values, bitset);
                return;
            case float?[] values:
                InsertOptionalValues(column, values, bitset);
                return;
            case double[] values:
                InsertValues(column, values, bitset);
                return;
            case double?[] values:
                InsertOptionalValues(column, values, bitset);
                return;
            case byte[][] values:
                InsertValues(column, values, bitset);
                return;
            case ReadOnlyMemory<byte>[] values:
                InsertValues(column, values, bitset);
                return;
            case ReadOnlyMemory<byte>?[] values:
                InsertOptionalValues(column, values, bitset);
                return;
            case not Array:
                InsertObject(column, value, bitset);
                return;
        }

        var array = (Array)value;
        for (var i = 0; i < array.Length; i++)
            InsertRepeatedValue(column, array.GetValue(i), bitset);
    }

    static bool IsBinaryLeaf(Column column, object value)
        => value is byte[] && column.PhysicalType is ParquetPhysicalType.ByteArray
            or ParquetPhysicalType.FixedLenByteArray or ParquetPhysicalType.Int96;

    static void InsertValues<T>(Column column, ReadOnlySpan<T> values, Span<byte> bitset)
        where T : notnull
    {
        if (typeof(T) == typeof(int))
        {
            var typed = Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<int>>(ref values);
            for (var i = 0; i < typed.Length; i++)
                InsertHash(bitset, ParquetBloomFilterHash.Hash(typed[i]));
            return;
        }
        if (typeof(T) == typeof(long))
        {
            var typed = Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<long>>(ref values);
            for (var i = 0; i < typed.Length; i++)
                InsertHash(bitset, ParquetBloomFilterHash.Hash(typed[i]));
            return;
        }
        if (typeof(T) == typeof(float))
        {
            var typed = Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<float>>(ref values);
            for (var i = 0; i < typed.Length; i++)
                InsertHash(bitset, ParquetBloomFilterHash.Hash(typed[i]));
            return;
        }
        if (typeof(T) == typeof(double))
        {
            var typed = Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<double>>(ref values);
            for (var i = 0; i < typed.Length; i++)
                InsertHash(bitset, ParquetBloomFilterHash.Hash(typed[i]));
            return;
        }
        if (typeof(T) == typeof(Guid))
        {
            var typed = Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<Guid>>(ref values);
            for (var i = 0; i < typed.Length; i++)
                InsertHash(bitset, ParquetBloomFilterHash.Hash(typed[i]));
            return;
        }
        if (typeof(T) == typeof(byte[]))
        {
            var typed = Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<byte[]>>(ref values);
            for (var i = 0; i < typed.Length; i++)
                if (typed[i] is { } value)
                    InsertHash(bitset, ParquetBloomFilterHash.Hash(value));
            return;
        }
        if (typeof(T) == typeof(ReadOnlyMemory<byte>))
        {
            var typed = Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<ReadOnlyMemory<byte>>>(ref values);
            for (var i = 0; i < typed.Length; i++)
                InsertHash(bitset, ParquetBloomFilterHash.Hash(typed[i].Span));
            return;
        }

        throw new NotSupportedException(
            $"Column '{column.Name}' cannot build a Bloom filter from values of type '{typeof(T)}'.");
    }

    static void InsertOptionalValues<T>(Column column, ReadOnlySpan<T?> values, Span<byte> bitset)
        where T : struct
    {
        if (typeof(T) == typeof(int))
        {
            var typed = Unsafe.As<ReadOnlySpan<T?>, ReadOnlySpan<int?>>(ref values);
            for (var i = 0; i < typed.Length; i++)
                if (typed[i] is { } value)
                    InsertHash(bitset, ParquetBloomFilterHash.Hash(value));
            return;
        }
        if (typeof(T) == typeof(long))
        {
            var typed = Unsafe.As<ReadOnlySpan<T?>, ReadOnlySpan<long?>>(ref values);
            for (var i = 0; i < typed.Length; i++)
                if (typed[i] is { } value)
                    InsertHash(bitset, ParquetBloomFilterHash.Hash(value));
            return;
        }
        if (typeof(T) == typeof(float))
        {
            var typed = Unsafe.As<ReadOnlySpan<T?>, ReadOnlySpan<float?>>(ref values);
            for (var i = 0; i < typed.Length; i++)
                if (typed[i] is { } value)
                    InsertHash(bitset, ParquetBloomFilterHash.Hash(value));
            return;
        }
        if (typeof(T) == typeof(double))
        {
            var typed = Unsafe.As<ReadOnlySpan<T?>, ReadOnlySpan<double?>>(ref values);
            for (var i = 0; i < typed.Length; i++)
                if (typed[i] is { } value)
                    InsertHash(bitset, ParquetBloomFilterHash.Hash(value));
            return;
        }
        if (typeof(T) == typeof(Guid))
        {
            var typed = Unsafe.As<ReadOnlySpan<T?>, ReadOnlySpan<Guid?>>(ref values);
            for (var i = 0; i < typed.Length; i++)
                if (typed[i] is { } value)
                    InsertHash(bitset, ParquetBloomFilterHash.Hash(value));
            return;
        }
        if (typeof(T) == typeof(ReadOnlyMemory<byte>))
        {
            var typed = Unsafe.As<ReadOnlySpan<T?>, ReadOnlySpan<ReadOnlyMemory<byte>?>>(ref values);
            for (var i = 0; i < typed.Length; i++)
                if (typed[i] is { } value)
                    InsertHash(bitset, ParquetBloomFilterHash.Hash(value.Span));
            return;
        }

        for (var i = 0; i < values.Length; i++)
            if (values[i] is { } value)
                InsertObject(column, value, bitset);
    }

    static void InsertObject(Column column, object value, Span<byte> bitset)
    {
        var hash = value switch
        {
            byte byteValue when column.PhysicalType == ParquetPhysicalType.Int32
                => ParquetBloomFilterHash.Hash((int)byteValue),
            ushort ushortValue when column.PhysicalType == ParquetPhysicalType.Int32
                => ParquetBloomFilterHash.Hash((int)ushortValue),
            int intValue when column.PhysicalType == ParquetPhysicalType.Int32
                => ParquetBloomFilterHash.Hash(intValue),
            uint uintValue when column.PhysicalType == ParquetPhysicalType.Int32
                => ParquetBloomFilterHash.Hash(uintValue),
            long longValue when column.PhysicalType == ParquetPhysicalType.Int64
                => ParquetBloomFilterHash.Hash(longValue),
            ulong ulongValue when column.PhysicalType == ParquetPhysicalType.Int64
                => ParquetBloomFilterHash.Hash(ulongValue),
            float floatValue when column.PhysicalType == ParquetPhysicalType.Float
                => ParquetBloomFilterHash.Hash(floatValue),
            double doubleValue when column.PhysicalType == ParquetPhysicalType.Double
                => ParquetBloomFilterHash.Hash(doubleValue),
            Guid guidValue when column.PhysicalType == ParquetPhysicalType.FixedLenByteArray
                => ParquetBloomFilterHash.Hash(guidValue),
            byte[] bytes when column.PhysicalType is ParquetPhysicalType.ByteArray
                or ParquetPhysicalType.FixedLenByteArray or ParquetPhysicalType.Int96
                => ParquetBloomFilterHash.Hash(bytes),
            ReadOnlyMemory<byte> memory when column.PhysicalType is ParquetPhysicalType.ByteArray
                or ParquetPhysicalType.FixedLenByteArray or ParquetPhysicalType.Int96
                => ParquetBloomFilterHash.Hash(memory.Span),
            _ => throw new NotSupportedException(
                $"Column '{column.Name}' cannot build a Bloom filter from values of type '{value.GetType()}'.")
        };
        InsertHash(bitset, hash);
    }

    static void InsertHash(Span<byte> bitset, ulong hash)
        => SplitBlockBloomFilter.InsertHash(bitset, hash);
}
