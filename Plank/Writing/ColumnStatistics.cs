using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Numerics;
using System.Text;
using Plank.Schema;
using TextEncoding = System.Text.Encoding;

namespace Plank.Writing;

internal readonly struct ColumnStatistics
{
    static readonly TextEncoding Utf8 = TextEncoding.UTF8;

    internal readonly byte[]? MinValue;
    internal readonly byte[]? MaxValue;
    internal readonly int MinValueLength;
    internal readonly int MaxValueLength;
    internal readonly ColumnStatisticsValueKind ValueKind;
    internal readonly long MinBits;
    internal readonly long MaxBits;
    internal readonly long NullCount;
    internal readonly long DistinctCount;
    internal readonly bool HasStatistics;

    ColumnStatistics(byte[]? minValue, byte[]? maxValue, long nullCount, bool hasStatistics)
        : this(minValue, minValue?.Length ?? 0, maxValue, maxValue?.Length ?? 0, nullCount, hasStatistics)
    {
    }

    ColumnStatistics(byte[]? minValue, int minValueLength, byte[]? maxValue, int maxValueLength, long nullCount,
        bool hasStatistics)
    {
        MinValue = minValue;
        MaxValue = maxValue;
        MinValueLength = minValue is null ? 0 : minValueLength;
        MaxValueLength = maxValue is null ? 0 : maxValueLength;
        ValueKind = minValue is null || maxValue is null ? ColumnStatisticsValueKind.None : ColumnStatisticsValueKind.Binary;
        MinBits = 0;
        MaxBits = 0;
        NullCount = nullCount;
        DistinctCount = minValue is null || maxValue is null
            ? 0
            : CompareBytes(minValue.AsSpan(0, MinValueLength), maxValue.AsSpan(0, MaxValueLength)) == 0 ? 1 : -1;
        HasStatistics = hasStatistics;
    }

    ColumnStatistics(ColumnStatisticsValueKind valueKind, long minBits, long maxBits, long nullCount, bool hasStatistics)
    {
        MinValue = null;
        MaxValue = null;
        MinValueLength = 0;
        MaxValueLength = 0;
        ValueKind = valueKind;
        MinBits = minBits;
        MaxBits = maxBits;
        NullCount = nullCount;
        DistinctCount = valueKind == ColumnStatisticsValueKind.None ? -1 : minBits == maxBits ? 1 : -1;
        HasStatistics = hasStatistics;
    }

    internal static ColumnStatistics Empty(long nullCount)
        => new(null, null, nullCount, true);

    internal ColumnStatistics WithNullCount(long nullCount)
        => ValueKind == ColumnStatisticsValueKind.Binary
            ? new ColumnStatistics(MinValue, MinValueLength, MaxValue, MaxValueLength, nullCount, HasStatistics)
            : new ColumnStatistics(ValueKind, MinBits, MaxBits, nullCount, HasStatistics);

    internal static ColumnStatistics Create<T>(Column column, ReadOnlySpan<T> values, long nullCount)
        where T : notnull
        => Create(column, values, nullCount, DefaultParquetBufferPool.Shared);

    internal static ColumnStatistics Create<T>(Column column, ReadOnlySpan<T> values, long nullCount,
        IParquetBufferPool bufferPool)
        where T : notnull
    {
        if (column.Options.Repetition == ParquetRepetition.Repeated)
            return CreateRepeated(column, values, nullCount);

        if (values.Length == 0)
            return Empty(nullCount);

        if (typeof(T) == typeof(bool))
            return CreateBoolean(AsSpan<T, bool>(values), nullCount);
        if (typeof(T) == typeof(int))
            return CreateInt32(AsSpan<T, int>(values), nullCount);
        if (typeof(T) == typeof(long))
            return CreateInt64(AsSpan<T, long>(values), nullCount);
        if (typeof(T) == typeof(float))
            return CreateFloat(AsSpan<T, float>(values), nullCount);
        if (typeof(T) == typeof(double))
            return CreateDouble(AsSpan<T, double>(values), nullCount);
        if (typeof(T) == typeof(byte[]))
            return CreateByteArray(column, AsAnySpan<T, byte[]>(values), nullCount);
        if (typeof(T) == typeof(ReadOnlyMemory<byte>))
            return CreateMemory(AsAnySpan<T, ReadOnlyMemory<byte>>(values), nullCount);
        if (typeof(T) == typeof(string))
            return CreateString(AsAnySpan<T, string>(values), nullCount, bufferPool);

        return Empty(nullCount);
    }

    internal static ColumnStatistics CreateWithReusableBinaryBuffers<T>(Column column, ReadOnlySpan<T> values,
        long nullCount, ref byte[]? minBuffer, ref byte[]? maxBuffer)
        where T : notnull
        => CreateWithReusableBinaryBuffers(column, values, nullCount, ref minBuffer, ref maxBuffer,
            DefaultParquetBufferPool.Shared);

    internal static ColumnStatistics CreateWithReusableBinaryBuffers<T>(Column column, ReadOnlySpan<T> values,
        long nullCount, ref byte[]? minBuffer, ref byte[]? maxBuffer, IParquetBufferPool bufferPool)
        where T : notnull
    {
        if (typeof(T) == typeof(byte[]))
            return CreateByteArray(column, AsAnySpan<T, byte[]>(values), nullCount, ref minBuffer, ref maxBuffer);
        if (typeof(T) == typeof(ReadOnlyMemory<byte>))
            return CreateMemory(AsAnySpan<T, ReadOnlyMemory<byte>>(values), nullCount, ref minBuffer, ref maxBuffer);
        if (typeof(T) == typeof(string))
            return CreateString(AsAnySpan<T, string>(values), nullCount, ref minBuffer, ref maxBuffer, bufferPool);

        return Create(column, values, nullCount, bufferPool);
    }

    static ColumnStatistics CreateRepeated<T>(Column column, ReadOnlySpan<T> values, long nullCount)
    {
        var accumulator = new RepeatedAccumulator(column);
        for (var i = 0; i < values.Length; i++)
            accumulator.AddNode(values[i]);

        return accumulator.ToStatistics(nullCount);
    }

    internal static ColumnStatistics CreateOptional<T>(Column column, ReadOnlySpan<T?> values)
        where T : struct
    {
        if (typeof(T) == typeof(bool))
            return CreateNullableBoolean(AsNullableSpan<T, bool>(values));
        if (typeof(T) == typeof(int))
            return CreateNullableInt32(AsNullableSpan<T, int>(values));
        if (typeof(T) == typeof(long))
            return CreateNullableInt64(AsNullableSpan<T, long>(values));
        if (typeof(T) == typeof(float))
            return CreateNullableFloat(AsNullableSpan<T, float>(values));
        if (typeof(T) == typeof(double))
            return CreateNullableDouble(AsNullableSpan<T, double>(values));
        if (typeof(T) == typeof(ReadOnlyMemory<byte>))
            return CreateNullableMemory(AsNullableSpan<T, ReadOnlyMemory<byte>>(values));

        return Empty(CountNulls(values));
    }

    internal static ColumnStatistics CreateOptional<T>(Column column, ReadOnlySpan<T> values)
        where T : class
        => CreateOptional(column, values, DefaultParquetBufferPool.Shared);

    internal static ColumnStatistics CreateOptional<T>(Column column, ReadOnlySpan<T> values,
        IParquetBufferPool bufferPool)
        where T : class
    {
        if (typeof(T) == typeof(byte[]))
            return CreateOptionalByteArray(column, AsAnySpan<T, byte[]>(values));
        if (typeof(T) == typeof(string))
            return CreateOptionalString(AsAnySpan<T, string>(values), bufferPool);

        return Empty(CountNulls(values));
    }

    internal static ColumnStatistics CreateByte(ReadOnlySpan<byte> values, long nullCount)
    {
        if (values.Length == 0)
            return Empty(nullCount);

        var min = values[0];
        var max = values[0];
        for (var i = 1; i < values.Length; i++)
        {
            var value = values[i];
            if (value < min)
                min = value;
            if (value > max)
                max = value;
        }

        return FromInt32(min, max, nullCount);
    }

    internal static ColumnStatistics CreateUInt16(ReadOnlySpan<ushort> values, long nullCount)
    {
        if (values.Length == 0)
            return Empty(nullCount);

        var min = values[0];
        var max = values[0];
        for (var i = 1; i < values.Length; i++)
        {
            var value = values[i];
            if (value < min)
                min = value;
            if (value > max)
                max = value;
        }

        return FromInt32(min, max, nullCount);
    }

    internal static ColumnStatistics CreateUInt32(ReadOnlySpan<uint> values, long nullCount)
    {
        if (values.Length == 0)
            return Empty(nullCount);

        var min = values[0];
        var max = values[0];
        for (var i = 1; i < values.Length; i++)
        {
            var value = values[i];
            if (value < min)
                min = value;
            if (value > max)
                max = value;
        }

        return FromUInt32(min, max, nullCount);
    }

    internal static ColumnStatistics CreateUInt64(ReadOnlySpan<ulong> values, long nullCount)
    {
        if (values.Length == 0)
            return Empty(nullCount);

        var min = values[0];
        var max = values[0];
        for (var i = 1; i < values.Length; i++)
        {
            var value = values[i];
            if (value < min)
                min = value;
            if (value > max)
                max = value;
        }

        return FromUInt64(min, max, nullCount);
    }

    internal static ColumnStatistics CreateNullableByte(ReadOnlySpan<byte?> values)
    {
        byte min = 0;
        byte max = 0;
        var hasValue = false;
        var nullCount = 0L;
        for (var i = 0; i < values.Length; i++)
        {
            if (values[i] is not { } value)
            {
                nullCount++;
                continue;
            }

            if (!hasValue)
            {
                min = value;
                max = value;
                hasValue = true;
                continue;
            }

            if (value < min)
                min = value;
            if (value > max)
                max = value;
        }

        return hasValue ? FromInt32(min, max, nullCount) : Empty(nullCount);
    }

    internal static ColumnStatistics CreateNullableUInt16(ReadOnlySpan<ushort?> values)
    {
        ushort min = 0;
        ushort max = 0;
        var hasValue = false;
        var nullCount = 0L;
        for (var i = 0; i < values.Length; i++)
        {
            if (values[i] is not { } value)
            {
                nullCount++;
                continue;
            }

            if (!hasValue)
            {
                min = value;
                max = value;
                hasValue = true;
                continue;
            }

            if (value < min)
                min = value;
            if (value > max)
                max = value;
        }

        return hasValue ? FromInt32(min, max, nullCount) : Empty(nullCount);
    }

    internal static ColumnStatistics CreateNullableUInt32(ReadOnlySpan<uint?> values)
    {
        uint min = 0;
        uint max = 0;
        var hasValue = false;
        var nullCount = 0L;
        for (var i = 0; i < values.Length; i++)
        {
            if (values[i] is not { } value)
            {
                nullCount++;
                continue;
            }

            if (!hasValue)
            {
                min = value;
                max = value;
                hasValue = true;
                continue;
            }

            if (value < min)
                min = value;
            if (value > max)
                max = value;
        }

        return hasValue ? FromUInt32(min, max, nullCount) : Empty(nullCount);
    }

    internal static ColumnStatistics CreateNullableUInt64(ReadOnlySpan<ulong?> values)
    {
        ulong min = 0;
        ulong max = 0;
        var hasValue = false;
        var nullCount = 0L;
        for (var i = 0; i < values.Length; i++)
        {
            if (values[i] is not { } value)
            {
                nullCount++;
                continue;
            }

            if (!hasValue)
            {
                min = value;
                max = value;
                hasValue = true;
                continue;
            }

            if (value < min)
                min = value;
            if (value > max)
                max = value;
        }

        return hasValue ? FromUInt64(min, max, nullCount) : Empty(nullCount);
    }

    static ColumnStatistics CreateBoolean(ReadOnlySpan<bool> values, long nullCount)
    {
        var min = true;
        var max = false;
        for (var i = 0; i < values.Length; i++)
        {
            var value = values[i];
            min &= value;
            max |= value;
            if (!min && max)
                break;
        }

        return new ColumnStatistics(ColumnStatisticsValueKind.Boolean, min ? 1 : 0, max ? 1 : 0, nullCount, true);
    }

    static ColumnStatistics CreateNullableBoolean(ReadOnlySpan<bool?> values)
    {
        var min = true;
        var max = false;
        var hasValue = false;
        var nullCount = 0L;
        for (var i = 0; i < values.Length; i++)
        {
            if (values[i] is not { } value)
            {
                nullCount++;
                continue;
            }

            min &= value;
            max |= value;
            hasValue = true;
        }

        return hasValue
            ? new ColumnStatistics(ColumnStatisticsValueKind.Boolean, min ? 1 : 0, max ? 1 : 0, nullCount, true)
            : Empty(nullCount);
    }

    static ColumnStatistics CreateInt32(ReadOnlySpan<int> values, long nullCount)
    {
        if (!TryGetInt32MinMax(values, out var min, out var max))
            return Empty(nullCount);

        return FromInt32(min, max, nullCount);
    }

    internal static bool TryGetInt32MinMax(ReadOnlySpan<int> values, out int min, out int max)
    {
        min = 0;
        max = 0;
        if (values.Length == 0)
            return false;
        if (Vector.IsHardwareAccelerated && values.Length >= Vector<int>.Count)
            return TryGetInt32MinMaxVectorized(values, out min, out max);

        min = values[0];
        max = values[0];
        for (var i = 1; i < values.Length; i++)
        {
            var value = values[i];
            if (value < min)
                min = value;
            if (value > max)
                max = value;
        }

        return true;
    }

    static bool TryGetInt32MinMaxVectorized(ReadOnlySpan<int> values, out int min, out int max)
    {
        var width = Vector<int>.Count;
        var minVector = new Vector<int>(values);
        var maxVector = minVector;
        var i = width;
        for (; i <= values.Length - width; i += width)
        {
            var current = new Vector<int>(values[i..]);
            minVector = Vector.Min(minVector, current);
            maxVector = Vector.Max(maxVector, current);
        }

        min = minVector[0];
        max = maxVector[0];
        for (var lane = 1; lane < width; lane++)
        {
            var minCandidate = minVector[lane];
            var maxCandidate = maxVector[lane];
            if (minCandidate < min)
                min = minCandidate;
            if (maxCandidate > max)
                max = maxCandidate;
        }

        for (; i < values.Length; i++)
        {
            var value = values[i];
            if (value < min)
                min = value;
            if (value > max)
                max = value;
        }

        return true;
    }

    static ColumnStatistics CreateNullableInt32(ReadOnlySpan<int?> values)
    {
        int min = 0;
        int max = 0;
        var hasValue = false;
        var nullCount = 0L;
        for (var i = 0; i < values.Length; i++)
        {
            if (values[i] is not { } value)
            {
                nullCount++;
                continue;
            }

            if (!hasValue)
            {
                min = value;
                max = value;
                hasValue = true;
                continue;
            }

            if (value < min)
                min = value;
            if (value > max)
                max = value;
        }

        return hasValue ? FromInt32(min, max, nullCount) : Empty(nullCount);
    }

    static ColumnStatistics CreateInt64(ReadOnlySpan<long> values, long nullCount)
    {
        if (!TryGetInt64MinMax(values, out var min, out var max))
            return Empty(nullCount);

        return FromInt64(min, max, nullCount);
    }

    static bool TryGetInt64MinMax(ReadOnlySpan<long> values, out long min, out long max)
    {
        min = 0;
        max = 0;
        if (values.Length == 0)
            return false;
        if (Vector.IsHardwareAccelerated && values.Length >= Vector<long>.Count)
            return TryGetInt64MinMaxVectorized(values, out min, out max);

        min = values[0];
        max = values[0];
        for (var i = 1; i < values.Length; i++)
        {
            var value = values[i];
            if (value < min)
                min = value;
            if (value > max)
                max = value;
        }

        return true;
    }

    static bool TryGetInt64MinMaxVectorized(ReadOnlySpan<long> values, out long min, out long max)
    {
        var width = Vector<long>.Count;
        var minVector = new Vector<long>(values);
        var maxVector = minVector;
        var i = width;
        for (; i <= values.Length - width; i += width)
        {
            var current = new Vector<long>(values[i..]);
            minVector = Vector.Min(minVector, current);
            maxVector = Vector.Max(maxVector, current);
        }

        min = minVector[0];
        max = maxVector[0];
        for (var lane = 1; lane < width; lane++)
        {
            var minCandidate = minVector[lane];
            var maxCandidate = maxVector[lane];
            if (minCandidate < min)
                min = minCandidate;
            if (maxCandidate > max)
                max = maxCandidate;
        }

        for (; i < values.Length; i++)
        {
            var value = values[i];
            if (value < min)
                min = value;
            if (value > max)
                max = value;
        }

        return true;
    }

    static ColumnStatistics CreateNullableInt64(ReadOnlySpan<long?> values)
    {
        long min = 0;
        long max = 0;
        var hasValue = false;
        var nullCount = 0L;
        for (var i = 0; i < values.Length; i++)
        {
            if (values[i] is not { } value)
            {
                nullCount++;
                continue;
            }

            if (!hasValue)
            {
                min = value;
                max = value;
                hasValue = true;
                continue;
            }

            if (value < min)
                min = value;
            if (value > max)
                max = value;
        }

        return hasValue ? FromInt64(min, max, nullCount) : Empty(nullCount);
    }

    static ColumnStatistics CreateFloat(ReadOnlySpan<float> values, long nullCount)
    {
        if (!TryGetFloatMinMax(values, out var min, out var max))
            return Empty(nullCount);

        return FromFloat(min, max, nullCount);
    }

    static ColumnStatistics CreateNullableFloat(ReadOnlySpan<float?> values)
    {
        Span<float> denseValues = values.Length <= 256 ? stackalloc float[values.Length] : new float[values.Length];
        var count = 0;
        var nullCount = 0L;
        for (var i = 0; i < values.Length; i++)
        {
            if (values[i] is { } value)
            {
                denseValues[count++] = value;
                continue;
            }

            nullCount++;
        }

        return TryGetFloatMinMax(denseValues[..count], out var min, out var max)
            ? FromFloat(min, max, nullCount)
            : Empty(nullCount);
    }

    static ColumnStatistics CreateDouble(ReadOnlySpan<double> values, long nullCount)
    {
        if (!TryGetDoubleMinMax(values, out var min, out var max))
            return Empty(nullCount);

        return FromDouble(min, max, nullCount);
    }

    static ColumnStatistics CreateNullableDouble(ReadOnlySpan<double?> values)
    {
        Span<double> denseValues = values.Length <= 256 ? stackalloc double[values.Length] : new double[values.Length];
        var count = 0;
        var nullCount = 0L;
        for (var i = 0; i < values.Length; i++)
        {
            if (values[i] is { } value)
            {
                denseValues[count++] = value;
                continue;
            }

            nullCount++;
        }

        return TryGetDoubleMinMax(denseValues[..count], out var min, out var max)
            ? FromDouble(min, max, nullCount)
            : Empty(nullCount);
    }

    static ColumnStatistics CreateByteArray(Column column, ReadOnlySpan<byte[]> values, long nullCount)
    {
        if (values.Length == 0)
            return Empty(nullCount);

        ReadOnlySpan<byte> min = values[0] ?? throw new InvalidOperationException($"Column '{column.Name}' does not support null values.");
        ReadOnlySpan<byte> max = min;
        for (var i = 1; i < values.Length; i++)
        {
            var value = values[i] ?? throw new InvalidOperationException($"Column '{column.Name}' does not support null values.");
            if (CompareBytes(value, min) < 0)
                min = value;
            if (CompareBytes(value, max) > 0)
                max = value;
        }

        return new ColumnStatistics(min.ToArray(), max.ToArray(), nullCount, true);
    }

    static ColumnStatistics CreateByteArray(Column column, ReadOnlySpan<byte[]> values, long nullCount,
        ref byte[]? minBuffer, ref byte[]? maxBuffer)
    {
        if (values.Length == 0)
            return Empty(nullCount);

        ReadOnlySpan<byte> min = values[0] ?? throw new InvalidOperationException($"Column '{column.Name}' does not support null values.");
        ReadOnlySpan<byte> max = min;
        for (var i = 1; i < values.Length; i++)
        {
            var value = values[i] ?? throw new InvalidOperationException($"Column '{column.Name}' does not support null values.");
            if (CompareBytes(value, min) < 0)
                min = value;
            if (CompareBytes(value, max) > 0)
                max = value;
        }

        CopyToReusableBuffer(min, ref minBuffer);
        CopyToReusableBuffer(max, ref maxBuffer);
        return new ColumnStatistics(minBuffer, min.Length, maxBuffer, max.Length, nullCount, true);
    }

    static ColumnStatistics CreateOptionalByteArray(Column column, ReadOnlySpan<byte[]> values)
    {
        byte[]? min = null;
        byte[]? max = null;
        var nullCount = 0L;
        for (var i = 0; i < values.Length; i++)
        {
            var value = values[i];
            if (value is null)
            {
                nullCount++;
                continue;
            }

            if (min is null)
            {
                min = value;
                max = value;
                continue;
            }

            if (CompareBytes(value, min) < 0)
                min = value;
            if (CompareBytes(value, max!) > 0)
                max = value;
        }

        return min is null ? Empty(nullCount) : new ColumnStatistics(min.ToArray(), max!.ToArray(), nullCount, true);
    }

    static ColumnStatistics CreateMemory(ReadOnlySpan<ReadOnlyMemory<byte>> values, long nullCount)
    {
        if (values.Length == 0)
            return Empty(nullCount);

        var min = values[0].Span;
        var max = min;
        for (var i = 1; i < values.Length; i++)
        {
            var value = values[i].Span;
            if (CompareBytes(value, min) < 0)
                min = value;
            if (CompareBytes(value, max) > 0)
                max = value;
        }

        return new ColumnStatistics(min.ToArray(), max.ToArray(), nullCount, true);
    }

    static ColumnStatistics CreateMemory(ReadOnlySpan<ReadOnlyMemory<byte>> values, long nullCount,
        ref byte[]? minBuffer, ref byte[]? maxBuffer)
    {
        if (values.Length == 0)
            return Empty(nullCount);

        var min = values[0].Span;
        var max = min;
        for (var i = 1; i < values.Length; i++)
        {
            var value = values[i].Span;
            if (CompareBytes(value, min) < 0)
                min = value;
            if (CompareBytes(value, max) > 0)
                max = value;
        }

        CopyToReusableBuffer(min, ref minBuffer);
        CopyToReusableBuffer(max, ref maxBuffer);
        return new ColumnStatistics(minBuffer, min.Length, maxBuffer, max.Length, nullCount, true);
    }

    static ColumnStatistics CreateNullableMemory(ReadOnlySpan<ReadOnlyMemory<byte>?> values)
    {
        byte[]? min = null;
        byte[]? max = null;
        var nullCount = 0L;
        for (var i = 0; i < values.Length; i++)
        {
            if (values[i] is not { } memory)
            {
                nullCount++;
                continue;
            }

            var value = memory.Span;
            if (min is null)
            {
                min = value.ToArray();
                max = min;
                continue;
            }

            if (CompareBytes(value, min) < 0)
                min = value.ToArray();
            if (CompareBytes(value, max!) > 0)
                max = value.ToArray();
        }

        return min is null ? Empty(nullCount) : new ColumnStatistics(min, max, nullCount, true);
    }

    static ColumnStatistics CreateString(ReadOnlySpan<string> values, long nullCount, IParquetBufferPool bufferPool)
    {
        if (values.Length == 0)
            return Empty(nullCount);

        var min = values[0] ?? throw new InvalidOperationException("Required string column does not support null values.");
        var max = min;
        for (var i = 1; i < values.Length; i++)
        {
            var value = values[i] ?? throw new InvalidOperationException("Required string column does not support null values.");
            if (CompareUtf8Strings(value, min, bufferPool) < 0)
                min = value;
            if (CompareUtf8Strings(value, max, bufferPool) > 0)
                max = value;
        }

        return new ColumnStatistics(Utf8.GetBytes(min), Utf8.GetBytes(max), nullCount, true);
    }

    static ColumnStatistics CreateString(ReadOnlySpan<string> values, long nullCount, ref byte[]? minBuffer,
        ref byte[]? maxBuffer, IParquetBufferPool bufferPool)
    {
        if (values.Length == 0)
            return Empty(nullCount);

        var min = values[0] ?? throw new InvalidOperationException("Required string column does not support null values.");
        var max = min;
        for (var i = 1; i < values.Length; i++)
        {
            var value = values[i] ?? throw new InvalidOperationException("Required string column does not support null values.");
            if (CompareUtf8Strings(value, min, bufferPool) < 0)
                min = value;
            if (CompareUtf8Strings(value, max, bufferPool) > 0)
                max = value;
        }

        var minLength = CopyUtf8ToReusableBuffer(min, ref minBuffer);
        var maxLength = CopyUtf8ToReusableBuffer(max, ref maxBuffer);
        return new ColumnStatistics(minBuffer, minLength, maxBuffer, maxLength, nullCount, true);
    }

    static ColumnStatistics CreateOptionalString(ReadOnlySpan<string> values, IParquetBufferPool bufferPool)
    {
        string? min = null;
        string? max = null;
        var nullCount = 0L;
        for (var i = 0; i < values.Length; i++)
        {
            var value = values[i];
            if (value is null)
            {
                nullCount++;
                continue;
            }

            if (min is null)
            {
                min = value;
                max = value;
                continue;
            }

            if (CompareUtf8Strings(value, min, bufferPool) < 0)
                min = value;
            if (CompareUtf8Strings(value, max!, bufferPool) > 0)
                max = value;
        }

        return min is null ? Empty(nullCount) : new ColumnStatistics(Utf8.GetBytes(min), Utf8.GetBytes(max!), nullCount, true);
    }

    static int CompareUtf8Strings(string left, string right, IParquetBufferPool bufferPool)
    {
        var length = Math.Min(left.Length, right.Length);
        for (var i = 0; i < length; i++)
        {
            var leftChar = left[i];
            var rightChar = right[i];
            if (leftChar > 0x7F || rightChar > 0x7F)
                return CompareUtf8StringsSlow(left, right, bufferPool);
            var comparison = leftChar.CompareTo(rightChar);
            if (comparison != 0)
                return comparison;
        }

        return left.Length.CompareTo(right.Length);
    }

    static int CompareUtf8StringsSlow(string left, string right, IParquetBufferPool bufferPool)
    {
        var leftByteCount = Utf8.GetByteCount(left);
        var rightByteCount = Utf8.GetByteCount(right);
        var leftBytes = bufferPool.Rent<byte>(checked((uint)Math.Max(1, leftByteCount)));
        var rightBytes = bufferPool.Rent<byte>(checked((uint)Math.Max(1, rightByteCount)));
        try
        {
            Utf8.GetBytes(left, leftBytes.AsSpan(0, leftByteCount));
            Utf8.GetBytes(right, rightBytes.AsSpan(0, rightByteCount));
            return CompareBytes(leftBytes.AsSpan(0, leftByteCount), rightBytes.AsSpan(0, rightByteCount));
        }
        finally
        {
            bufferPool.Return(leftBytes);
            bufferPool.Return(rightBytes);
        }
    }

    static bool TryGetFloatMinMax(ReadOnlySpan<float> values, out float min, out float max)
    {
        min = 0;
        max = 0;
        if (values.Length == 0)
            return false;
        if (Vector.IsHardwareAccelerated && values.Length >= Vector<float>.Count)
            return TryGetFloatMinMaxVectorized(values, out min, out max);

        var first = values[0];
        if (float.IsNaN(first))
            return TryGetFloatMinMaxScalar(values, out min, out max);

        min = first;
        max = first;

        for (var i = 1; i < values.Length; i++)
        {
            var value = values[i];
            if (float.IsNaN(value))
                continue;

            if (value < min)
                min = value;
            if (value > max)
                max = value;
        }

        return true;
    }

    static bool TryGetDoubleMinMax(ReadOnlySpan<double> values, out double min, out double max)
    {
        min = 0;
        max = 0;
        if (values.Length == 0)
            return false;
        if (Vector.IsHardwareAccelerated && values.Length >= Vector<double>.Count)
            return TryGetDoubleMinMaxVectorized(values, out min, out max);

        var first = values[0];
        if (double.IsNaN(first))
            return TryGetDoubleMinMaxScalar(values, out min, out max);

        min = first;
        max = first;

        for (var i = 1; i < values.Length; i++)
        {
            var value = values[i];
            if (double.IsNaN(value))
                continue;

            if (value < min)
                min = value;
            if (value > max)
                max = value;
        }

        return true;
    }

    static bool TryGetFloatMinMaxVectorized(ReadOnlySpan<float> values, out float min, out float max)
    {
        var width = Vector<float>.Count;
        var first = new Vector<float>(values);
        if (!Vector.EqualsAll(first, first))
            return TryGetFloatMinMaxScalar(values, out min, out max);

        var minVector = first;
        var maxVector = first;
        var i = width;
        for (; i <= values.Length - width; i += width)
        {
            var current = new Vector<float>(values[i..]);
            if (!Vector.EqualsAll(current, current))
                return TryGetFloatMinMaxScalar(values, out min, out max);

            minVector = Vector.Min(minVector, current);
            maxVector = Vector.Max(maxVector, current);
        }

        min = minVector[0];
        max = maxVector[0];
        for (var lane = 1; lane < width; lane++)
        {
            var minCandidate = minVector[lane];
            var maxCandidate = maxVector[lane];
            if (minCandidate < min)
                min = minCandidate;
            if (maxCandidate > max)
                max = maxCandidate;
        }

        for (; i < values.Length; i++)
        {
            var value = values[i];
            if (float.IsNaN(value))
                return TryGetFloatMinMaxScalar(values, out min, out max);
            if (value < min)
                min = value;
            if (value > max)
                max = value;
        }

        return true;
    }

    static bool TryGetDoubleMinMaxVectorized(ReadOnlySpan<double> values, out double min, out double max)
    {
        var width = Vector<double>.Count;
        var first = new Vector<double>(values);
        if (!Vector.EqualsAll(first, first))
            return TryGetDoubleMinMaxScalar(values, out min, out max);

        var minVector = first;
        var maxVector = first;
        var i = width;
        for (; i <= values.Length - width; i += width)
        {
            var current = new Vector<double>(values[i..]);
            if (!Vector.EqualsAll(current, current))
                return TryGetDoubleMinMaxScalar(values, out min, out max);

            minVector = Vector.Min(minVector, current);
            maxVector = Vector.Max(maxVector, current);
        }

        min = minVector[0];
        max = maxVector[0];
        for (var lane = 1; lane < width; lane++)
        {
            var minCandidate = minVector[lane];
            var maxCandidate = maxVector[lane];
            if (minCandidate < min)
                min = minCandidate;
            if (maxCandidate > max)
                max = maxCandidate;
        }

        for (; i < values.Length; i++)
        {
            var value = values[i];
            if (double.IsNaN(value))
                return TryGetDoubleMinMaxScalar(values, out min, out max);
            if (value < min)
                min = value;
            if (value > max)
                max = value;
        }

        return true;
    }

    static bool TryGetFloatMinMaxScalar(ReadOnlySpan<float> values, out float min, out float max)
    {
        min = 0;
        max = 0;
        var hasValue = false;
        for (var i = 0; i < values.Length; i++)
        {
            var value = values[i];
            if (float.IsNaN(value))
                continue;

            if (!hasValue)
            {
                min = value;
                max = value;
                hasValue = true;
                continue;
            }

            if (value < min)
                min = value;
            if (value > max)
                max = value;
        }

        return hasValue;
    }

    static bool TryGetDoubleMinMaxScalar(ReadOnlySpan<double> values, out double min, out double max)
    {
        min = 0;
        max = 0;
        var hasValue = false;
        for (var i = 0; i < values.Length; i++)
        {
            var value = values[i];
            if (double.IsNaN(value))
                continue;

            if (!hasValue)
            {
                min = value;
                max = value;
                hasValue = true;
                continue;
            }

            if (value < min)
                min = value;
            if (value > max)
                max = value;
        }

        return hasValue;
    }

    internal static ColumnStatistics FromInt32(int min, int max, long nullCount)
        => new(ColumnStatisticsValueKind.Int32, min, max, nullCount, true);

    static ColumnStatistics FromUInt32(uint min, uint max, long nullCount)
        => new(ColumnStatisticsValueKind.UInt32, min, max, nullCount, true);

    static ColumnStatistics FromInt64(long min, long max, long nullCount)
        => new(ColumnStatisticsValueKind.Int64, min, max, nullCount, true);

    static ColumnStatistics FromUInt64(ulong min, ulong max, long nullCount)
        => new(ColumnStatisticsValueKind.UInt64, unchecked((long)min), unchecked((long)max), nullCount, true);

    static ColumnStatistics FromFloat(float min, float max, long nullCount)
        => new(ColumnStatisticsValueKind.Float, BitConverter.SingleToInt32Bits(min),
            BitConverter.SingleToInt32Bits(max), nullCount, true);

    static ColumnStatistics FromDouble(double min, double max, long nullCount)
        => new(ColumnStatisticsValueKind.Double, BitConverter.DoubleToInt64Bits(min),
            BitConverter.DoubleToInt64Bits(max), nullCount, true);

    static int CompareBytes(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        var length = Math.Min(left.Length, right.Length);
        for (var i = 0; i < length; i++)
        {
            var comparison = left[i].CompareTo(right[i]);
            if (comparison != 0)
                return comparison;
        }

        return left.Length.CompareTo(right.Length);
    }

    static void CopyToReusableBuffer(ReadOnlySpan<byte> source, ref byte[]? buffer)
    {
        if (buffer is null || buffer.Length < source.Length)
            buffer = new byte[source.Length];
        source.CopyTo(buffer);
    }

    static int CopyUtf8ToReusableBuffer(string source, ref byte[]? buffer)
    {
        var byteCount = Utf8.GetByteCount(source);
        if (buffer is null || buffer.Length < byteCount)
            buffer = new byte[byteCount];
        return Utf8.GetBytes(source, buffer);
    }

    static byte[] GetRequiredUtf8(string value)
        => Utf8.GetBytes(value ?? throw new InvalidOperationException("Required string column does not support null values."));

    static long CountNulls<T>(ReadOnlySpan<T?> values)
        where T : struct
    {
        var count = 0L;
        for (var i = 0; i < values.Length; i++)
            if (!values[i].HasValue)
                count++;

        return count;
    }

    static long CountNulls<T>(ReadOnlySpan<T> values)
        where T : class
    {
        var count = 0L;
        for (var i = 0; i < values.Length; i++)
            if (values[i] is null)
                count++;

        return count;
    }

    static ReadOnlySpan<TTo> AsSpan<TFrom, TTo>(ReadOnlySpan<TFrom> values)
        where TTo : struct
    {
        ref var first = ref Unsafe.As<TFrom, TTo>(ref MemoryMarshal.GetReference(values));
        return MemoryMarshal.CreateReadOnlySpan(ref first, values.Length);
    }

    static ReadOnlySpan<TTo> AsAnySpan<TFrom, TTo>(ReadOnlySpan<TFrom> values)
    {
        ref var first = ref Unsafe.As<TFrom, TTo>(ref MemoryMarshal.GetReference(values));
        return MemoryMarshal.CreateReadOnlySpan(ref first, values.Length);
    }

    static ReadOnlySpan<TTo?> AsNullableSpan<TFrom, TTo>(ReadOnlySpan<TFrom?> values)
        where TFrom : struct
        where TTo : struct
    {
        ref var first = ref Unsafe.As<TFrom?, TTo?>(ref MemoryMarshal.GetReference(values));
        return MemoryMarshal.CreateReadOnlySpan(ref first, values.Length);
    }

    internal enum ColumnStatisticsValueKind
    {
        None,
        Boolean,
        Int32,
        UInt32,
        Int64,
        UInt64,
        Float,
        Double,
        Binary
    }

    struct RepeatedAccumulator
    {
        readonly Column _column;
        bool _hasValue;
        bool _minBool;
        bool _maxBool;
        int _minInt32;
        int _maxInt32;
        uint _minUInt32;
        uint _maxUInt32;
        long _minInt64;
        long _maxInt64;
        ulong _minUInt64;
        ulong _maxUInt64;
        float _minFloat;
        float _maxFloat;
        double _minDouble;
        double _maxDouble;
        byte[]? _minBytes;
        byte[]? _maxBytes;

        internal RepeatedAccumulator(Column column)
        {
            _column = column;
            _hasValue = false;
            _minBool = true;
            _maxBool = false;
            _minInt32 = 0;
            _maxInt32 = 0;
            _minUInt32 = 0;
            _maxUInt32 = 0;
            _minInt64 = 0;
            _maxInt64 = 0;
            _minUInt64 = 0;
            _maxUInt64 = 0;
            _minFloat = 0;
            _maxFloat = 0;
            _minDouble = 0;
            _maxDouble = 0;
            _minBytes = null;
            _maxBytes = null;
        }

        internal void AddNode(object? value)
        {
            if (value is null)
                return;

            if (value is Array array)
            {
                for (var i = 0; i < array.Length; i++)
                    AddNode(array.GetValue(i));
                return;
            }

            AddLeaf(value);
        }

        internal ColumnStatistics ToStatistics(long nullCount)
            => !_hasValue ? Empty(nullCount) : _column.PhysicalType switch
            {
                ParquetPhysicalType.Boolean => new ColumnStatistics(ColumnStatisticsValueKind.Boolean,
                    _minBool ? 1 : 0, _maxBool ? 1 : 0, nullCount, true),
                ParquetPhysicalType.Int32 when _column.LogicalType is LogicalType.Int { IsSigned: false }
                    => FromUInt32(_minUInt32, _maxUInt32, nullCount),
                ParquetPhysicalType.Int32 => FromInt32(_minInt32, _maxInt32, nullCount),
                ParquetPhysicalType.Int64 when _column.LogicalType is LogicalType.Int { IsSigned: false }
                    => FromUInt64(_minUInt64, _maxUInt64, nullCount),
                ParquetPhysicalType.Int64 => FromInt64(_minInt64, _maxInt64, nullCount),
                ParquetPhysicalType.Float => FromFloat(_minFloat, _maxFloat, nullCount),
                ParquetPhysicalType.Double => FromDouble(_minDouble, _maxDouble, nullCount),
                ParquetPhysicalType.ByteArray or ParquetPhysicalType.FixedLenByteArray or ParquetPhysicalType.Int96
                    => _minBytes is null ? Empty(nullCount) : new ColumnStatistics(_minBytes, _maxBytes, nullCount, true),
                _ => Empty(nullCount)
            };

        void AddLeaf(object value)
        {
            switch (_column.PhysicalType)
            {
                case ParquetPhysicalType.Boolean:
                    AddBoolean((bool)value);
                    return;
                case ParquetPhysicalType.Int32:
                    AddInt32Leaf(value);
                    return;
                case ParquetPhysicalType.Int64:
                    AddInt64Leaf(value);
                    return;
                case ParquetPhysicalType.Float:
                    AddFloat((float)value);
                    return;
                case ParquetPhysicalType.Double:
                    AddDouble((double)value);
                    return;
                case ParquetPhysicalType.ByteArray:
                case ParquetPhysicalType.FixedLenByteArray:
                case ParquetPhysicalType.Int96:
                    AddBinaryLeaf(value);
                    return;
            }
        }

        void AddBoolean(bool value)
        {
            if (!_hasValue)
            {
                _minBool = value;
                _maxBool = value;
                _hasValue = true;
                return;
            }

            _minBool &= value;
            _maxBool |= value;
        }

        void AddInt32Leaf(object value)
        {
            if (_column.LogicalType is LogicalType.Int { IsSigned: false })
            {
                AddUInt32(value switch
                {
                    byte byteValue => byteValue,
                    ushort ushortValue => ushortValue,
                    uint uintValue => uintValue,
                    int intValue => unchecked((uint)intValue),
                    _ => Convert.ToUInt32(value)
                });
                return;
            }

            AddInt32(value switch
            {
                byte byteValue => byteValue,
                ushort ushortValue => ushortValue,
                int intValue => intValue,
                uint uintValue => unchecked((int)uintValue),
                DateOnly date => date.DayNumber - new DateOnly(1970, 1, 1).DayNumber,
                _ => Convert.ToInt32(value)
            });
        }

        void AddInt64Leaf(object value)
        {
            if (_column.LogicalType is LogicalType.Int { IsSigned: false })
            {
                AddUInt64(value switch
                {
                    ulong ulongValue => ulongValue,
                    long longValue => unchecked((ulong)longValue),
                    _ => Convert.ToUInt64(value)
                });
                return;
            }

            AddInt64(value switch
            {
                long longValue => longValue,
                ulong ulongValue => unchecked((long)ulongValue),
                DateTime dateTime => ToUnixTimeForStatistics(dateTime),
                DateTimeOffset dateTimeOffset => ToUnixTimeForStatistics(dateTimeOffset.UtcDateTime),
                TimeOnly time => ToTimeValueForStatistics(time),
                _ => Convert.ToInt64(value)
            });
        }

        void AddInt32(int value)
        {
            if (!_hasValue)
            {
                _minInt32 = value;
                _maxInt32 = value;
                _hasValue = true;
                return;
            }

            if (value < _minInt32)
                _minInt32 = value;
            if (value > _maxInt32)
                _maxInt32 = value;
        }

        void AddUInt32(uint value)
        {
            if (!_hasValue)
            {
                _minUInt32 = value;
                _maxUInt32 = value;
                _hasValue = true;
                return;
            }

            if (value < _minUInt32)
                _minUInt32 = value;
            if (value > _maxUInt32)
                _maxUInt32 = value;
        }

        void AddInt64(long value)
        {
            if (!_hasValue)
            {
                _minInt64 = value;
                _maxInt64 = value;
                _hasValue = true;
                return;
            }

            if (value < _minInt64)
                _minInt64 = value;
            if (value > _maxInt64)
                _maxInt64 = value;
        }

        void AddUInt64(ulong value)
        {
            if (!_hasValue)
            {
                _minUInt64 = value;
                _maxUInt64 = value;
                _hasValue = true;
                return;
            }

            if (value < _minUInt64)
                _minUInt64 = value;
            if (value > _maxUInt64)
                _maxUInt64 = value;
        }

        void AddFloat(float value)
        {
            if (float.IsNaN(value))
                return;

            if (!_hasValue)
            {
                _minFloat = value;
                _maxFloat = value;
                _hasValue = true;
                return;
            }

            if (value < _minFloat)
                _minFloat = value;
            if (value > _maxFloat)
                _maxFloat = value;
        }

        void AddDouble(double value)
        {
            if (double.IsNaN(value))
                return;

            if (!_hasValue)
            {
                _minDouble = value;
                _maxDouble = value;
                _hasValue = true;
                return;
            }

            if (value < _minDouble)
                _minDouble = value;
            if (value > _maxDouble)
                _maxDouble = value;
        }

        void AddBinaryLeaf(object value)
        {
            var bytes = value switch
            {
                byte[] array => array,
                ReadOnlyMemory<byte> memory => memory.ToArray(),
                string text => Utf8.GetBytes(text),
                _ => throw new InvalidOperationException(
                    $"Column '{_column.Name}' has unsupported repeated binary value type '{value.GetType()}'.")
            };

            if (!_hasValue)
            {
                _minBytes = bytes.ToArray();
                _maxBytes = bytes.ToArray();
                _hasValue = true;
                return;
            }

            if (CompareBytes(bytes, _minBytes) < 0)
                _minBytes = bytes.ToArray();
            if (CompareBytes(bytes, _maxBytes) > 0)
                _maxBytes = bytes.ToArray();
        }

        long ToUnixTimeForStatistics(DateTime value)
        {
            if (value.Kind != DateTimeKind.Utc)
                throw new InvalidOperationException(
                    $"DateTime values must have kind '{DateTimeKind.Utc}', got '{value.Kind}'.");

            var unit = _column.LogicalType is LogicalType.Timestamp timestamp ? timestamp.Unit : TimeUnit.Micros;
            var deltaTicks = value.Ticks - DateTime.UnixEpoch.Ticks;
            return unit switch
            {
                TimeUnit.Millis => deltaTicks / TimeSpan.TicksPerMillisecond,
                TimeUnit.Micros => deltaTicks / 10,
                TimeUnit.Nanos => checked(deltaTicks * 100),
                _ => throw new ArgumentOutOfRangeException(nameof(unit), unit, "Time unit must be a defined TimeUnit value.")
            };
        }

        long ToTimeValueForStatistics(TimeOnly value)
        {
            var unit = _column.LogicalType is LogicalType.Time time ? time.Unit : TimeUnit.Micros;
            return unit switch
            {
                TimeUnit.Millis => value.Ticks / TimeSpan.TicksPerMillisecond,
                TimeUnit.Micros => value.Ticks / 10,
                TimeUnit.Nanos => checked(value.Ticks * 100),
                _ => throw new ArgumentOutOfRangeException(nameof(unit), unit, "Time unit must be a defined TimeUnit value.")
            };
        }
    }
}
