using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Plank.Schema;

namespace Plank.Writing;

internal readonly struct ColumnStatistics
{
    internal readonly byte[]? MinValue;
    internal readonly byte[]? MaxValue;
    internal readonly ParquetBuffer MinValueBuffer;
    internal readonly ParquetBuffer MaxValueBuffer;
    internal readonly int MinValueLength;
    internal readonly int MaxValueLength;
    internal readonly ColumnStatisticsValueKind ValueKind;
    internal readonly long MinBits;
    internal readonly long MaxBits;
    internal readonly long NullCount;
    internal readonly long DistinctCount;
    internal readonly long NanCount;
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
        MinValueBuffer = default;
        MaxValueBuffer = default;
        MinValueLength = minValue is null ? 0 : minValueLength;
        MaxValueLength = maxValue is null ? 0 : maxValueLength;
        ValueKind = minValue is null || maxValue is null ? ColumnStatisticsValueKind.None : ColumnStatisticsValueKind.Binary;
        MinBits = 0;
        MaxBits = 0;
        NullCount = nullCount;
        DistinctCount = minValue is null || maxValue is null
            ? 0
            : minValue.AsSpan(0, MinValueLength).SequenceCompareTo(maxValue.AsSpan(0, MaxValueLength)) == 0 ? 1 : -1;
        NanCount = -1;
        HasStatistics = hasStatistics;
    }

    ColumnStatistics(ParquetBuffer minValue, int minValueLength, ParquetBuffer maxValue, int maxValueLength,
        long nullCount, bool hasStatistics)
    {
        MinValue = null;
        MaxValue = null;
        MinValueBuffer = minValue;
        MaxValueBuffer = maxValue;
        MinValueLength = minValueLength;
        MaxValueLength = maxValueLength;
        ValueKind = ColumnStatisticsValueKind.Binary;
        MinBits = 0;
        MaxBits = 0;
        NullCount = nullCount;
        DistinctCount = GetValueSpan(minValue, minValueLength)
            .SequenceCompareTo(GetValueSpan(maxValue, maxValueLength)) == 0 ? 1 : -1;
        NanCount = -1;
        HasStatistics = hasStatistics;
    }

    ColumnStatistics(ColumnStatisticsValueKind valueKind, long minBits, long maxBits, long nullCount,
        bool hasStatistics, long nanCount = -1)
    {
        MinValue = null;
        MaxValue = null;
        MinValueBuffer = default;
        MaxValueBuffer = default;
        MinValueLength = 0;
        MaxValueLength = 0;
        ValueKind = valueKind;
        MinBits = minBits;
        MaxBits = maxBits;
        NullCount = nullCount;
        DistinctCount = valueKind == ColumnStatisticsValueKind.None || nanCount > 0
            ? -1
            : minBits == maxBits ? 1 : -1;
        NanCount = nanCount;
        HasStatistics = hasStatistics;
    }

    internal static ColumnStatistics Empty(long nullCount)
        => new(null, null, nullCount, true);

    static ColumnStatistics EmptyFloating(long nullCount, long nanCount)
        => new(ColumnStatisticsValueKind.None, 0, 0, nullCount, true, nanCount);

    internal ColumnStatistics WithNullCount(long nullCount)
        => ValueKind == ColumnStatisticsValueKind.Binary
            ? HasNativeBinaryValues
                ? new ColumnStatistics(MinValueBuffer, MinValueLength, MaxValueBuffer, MaxValueLength, nullCount,
                    HasStatistics)
                : new ColumnStatistics(MinValue, MinValueLength, MaxValue, MaxValueLength, nullCount, HasStatistics)
            : new ColumnStatistics(ValueKind, MinBits, MaxBits, nullCount, HasStatistics, NanCount);

    internal static ColumnStatistics Create<T>(Column column, ReadOnlySpan<T> values, long nullCount)
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
        if (typeof(T) == typeof(decimal))
            return CreateDecimal(column, AsSpan<T, decimal>(values), nullCount);
        if (typeof(T) == typeof(byte[]))
            return CreateByteArray(column, AsAnySpan<T, byte[]>(values), nullCount);
        if (typeof(T) == typeof(ReadOnlyMemory<byte>))
            return CreateMemory(column, AsAnySpan<T, ReadOnlyMemory<byte>>(values), nullCount);
        return Empty(nullCount);
    }

    internal static ColumnStatistics CreateWithReusableBinaryBuffers<T>(Column column, ReadOnlySpan<T> values,
        long nullCount, ref ParquetBuffer minBuffer, ref ParquetBuffer maxBuffer, IParquetBufferPool bufferPool)
        where T : notnull
    {
        if (typeof(T) == typeof(byte[]))
            return CreateByteArray(column, AsAnySpan<T, byte[]>(values), nullCount, ref minBuffer, ref maxBuffer,
                bufferPool);
        if (typeof(T) == typeof(ReadOnlyMemory<byte>))
            return CreateMemory(column, AsAnySpan<T, ReadOnlyMemory<byte>>(values), nullCount, ref minBuffer,
                ref maxBuffer, bufferPool);
        if (typeof(T) == typeof(decimal))
            return CreateDecimal(column, AsSpan<T, decimal>(values), nullCount, ref minBuffer, ref maxBuffer,
                bufferPool);
        return Create(column, values, nullCount);
    }

    /// <summary>
    /// Builds binary statistics from the min and max a BYTE_ARRAY sizing or encode pass already found,
    /// instead of walking the values again.
    /// </summary>
    /// <remarks>
    /// The encoder orders values unsigned lexicographically, which is what
    /// <see cref="CompareBinary"/> does for every BYTE_ARRAY column except a decimal one. Callers must
    /// check <see cref="OrdersBinaryValuesLexicographically"/> before using an encoder-supplied result.
    /// </remarks>
    internal static ColumnStatistics CreateBinaryFromKnownExtremes<T>(Column column, ReadOnlySpan<T> values,
        int minIndex, int maxIndex, long nullCount, ref ParquetBuffer minBuffer, ref ParquetBuffer maxBuffer,
        IParquetBufferPool bufferPool)
        where T : notnull
    {
        ReadOnlySpan<byte> min;
        ReadOnlySpan<byte> max;
        if (typeof(T) == typeof(byte[]))
        {
            var byteArrays = AsAnySpan<T, byte[]>(values);
            min = byteArrays[minIndex];
            max = byteArrays[maxIndex];
        }
        else if (typeof(T) == typeof(ReadOnlyMemory<byte>))
        {
            var memories = AsAnySpan<T, ReadOnlyMemory<byte>>(values);
            min = memories[minIndex].Span;
            max = memories[maxIndex].Span;
        }
        else
        {
            throw new InvalidOperationException(
                $"Column '{column.Name}' cannot take binary statistics from values of type '{typeof(T)}'.");
        }

        CopyToReusableBuffer(min, ref minBuffer, bufferPool);
        CopyToReusableBuffer(max, ref maxBuffer, bufferPool);
        return new ColumnStatistics(minBuffer, min.Length, maxBuffer, max.Length, nullCount, true);
    }

    /// <summary>
    /// Whether the column's BYTE_ARRAY values compare as plain unsigned byte sequences. Decimals do
    /// not: their bytes are a two's complement number, so they are ordered by sign and magnitude.
    /// </summary>
    internal static bool OrdersBinaryValuesLexicographically(Column column)
    {
        ArgumentNullException.ThrowIfNull(column);
        return column.LogicalType is not LogicalType.Decimal;
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
        if (typeof(T) == typeof(decimal))
            return CreateNullableDecimal(column, AsNullableSpan<T, decimal>(values));
        if (typeof(T) == typeof(ReadOnlyMemory<byte>))
            return CreateNullableMemory(column, AsNullableSpan<T, ReadOnlyMemory<byte>>(values));

        return Empty(CountNulls(values));
    }

    internal static ColumnStatistics CreateOptional<T>(Column column, ReadOnlySpan<T> values)
        where T : class
    {
        if (typeof(T) == typeof(byte[]))
            return CreateOptionalByteArray(column, AsAnySpan<T, byte[]>(values));
        return Empty(CountNulls(values));
    }

    internal static ColumnStatistics CreateOptionalWithReusableBinaryBuffers<T>(Column column,
        ReadOnlySpan<T?> values, ref ParquetBuffer minBuffer, ref ParquetBuffer maxBuffer,
        IParquetBufferPool bufferPool)
        where T : struct
    {
        if (typeof(T) == typeof(ReadOnlyMemory<byte>))
            return CreateNullableMemory(column, AsNullableSpan<T, ReadOnlyMemory<byte>>(values), ref minBuffer,
                ref maxBuffer, bufferPool);
        if (typeof(T) == typeof(decimal))
            return CreateNullableDecimal(column, AsNullableSpan<T, decimal>(values), ref minBuffer, ref maxBuffer,
                bufferPool);
        return CreateOptional(column, values);
    }

    internal static ColumnStatistics CreateOptionalWithReusableBinaryBuffers<T>(Column column,
        ReadOnlySpan<T> values, ref ParquetBuffer minBuffer, ref ParquetBuffer maxBuffer,
        IParquetBufferPool bufferPool)
        where T : class
    {
        if (typeof(T) == typeof(byte[]))
            return CreateOptionalByteArray(column, AsAnySpan<T, byte[]>(values), ref minBuffer, ref maxBuffer,
                bufferPool);
        return CreateOptional(column, values);
    }

    internal ReadOnlySpan<byte> GetMinValue()
        => HasNativeBinaryValues
            ? GetValueSpan(MinValueBuffer, MinValueLength)
            : MinValue is null ? [] : MinValue.AsSpan(0, MinValueLength);

    internal ReadOnlySpan<byte> GetMaxValue()
        => HasNativeBinaryValues
            ? GetValueSpan(MaxValueBuffer, MaxValueLength)
            : MaxValue is null ? [] : MaxValue.AsSpan(0, MaxValueLength);

    internal static ColumnStatistics CreateByte(ReadOnlySpan<byte> values, long nullCount)
    {
        if (values.Length == 0)
            return Empty(nullCount);

        MinMaxScan.Compute(values, out var min, out var max);
        return FromInt32(min, max, nullCount);
    }

    internal static ColumnStatistics CreateUInt16(ReadOnlySpan<ushort> values, long nullCount)
    {
        if (values.Length == 0)
            return Empty(nullCount);

        MinMaxScan.Compute(values, out var min, out var max);
        return FromInt32(min, max, nullCount);
    }

    internal static ColumnStatistics CreateUInt32(ReadOnlySpan<uint> values, long nullCount)
    {
        if (values.Length == 0)
            return Empty(nullCount);

        MinMaxScan.Compute(values, out var min, out var max);
        return FromUInt32(min, max, nullCount);
    }

    internal static ColumnStatistics CreateUInt64(ReadOnlySpan<ulong> values, long nullCount)
    {
        if (values.Length == 0)
            return Empty(nullCount);

        MinMaxScan.Compute(values, out var min, out var max);
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
        MinMaxScan.Compute(values, out min, out max);
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

    internal static bool TryGetInt64MinMax(ReadOnlySpan<long> values, out long min, out long max)
    {
        min = 0;
        max = 0;
        if (values.Length == 0)
            return false;
        MinMaxScan.Compute(values, out min, out max);
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
        if (!TryGetFloatMinMax(values, out var min, out var max, out var nanCount))
            return EmptyFloating(nullCount, nanCount);

        return FromFloat(min, max, nullCount, nanCount);
    }

    static ColumnStatistics CreateNullableFloat(ReadOnlySpan<float?> values)
    {
        return TryGetNullableFloatMinMax(values, out var min, out var max, out var nullCount, out var nanCount)
            ? FromFloat(min, max, nullCount, nanCount)
            : EmptyFloating(nullCount, nanCount);
    }

    static ColumnStatistics CreateDouble(ReadOnlySpan<double> values, long nullCount)
    {
        if (!TryGetDoubleMinMax(values, out var min, out var max, out var nanCount))
            return EmptyFloating(nullCount, nanCount);

        return FromDouble(min, max, nullCount, nanCount);
    }

    static ColumnStatistics CreateNullableDouble(ReadOnlySpan<double?> values)
    {
        return TryGetNullableDoubleMinMax(values, out var min, out var max, out var nullCount, out var nanCount)
            ? FromDouble(min, max, nullCount, nanCount)
            : EmptyFloating(nullCount, nanCount);
    }

    static ColumnStatistics CreateDecimal(Column column, ReadOnlySpan<decimal> values, long nullCount)
    {
        if (!TryGetDecimalMinMax(values, out var min, out var max))
            return Empty(nullCount);

        var minValue = new byte[GetDecimalEncodedLength(column, min)];
        var maxValue = new byte[GetDecimalEncodedLength(column, max)];
        EncodeDecimal(column, min, minValue);
        EncodeDecimal(column, max, maxValue);
        return new ColumnStatistics(minValue, maxValue, nullCount, true);
    }

    static ColumnStatistics CreateDecimal(Column column, ReadOnlySpan<decimal> values, long nullCount,
        ref ParquetBuffer minBuffer, ref ParquetBuffer maxBuffer, IParquetBufferPool bufferPool)
    {
        if (!TryGetDecimalMinMax(values, out var min, out var max))
            return Empty(nullCount);

        var minLength = GetDecimalEncodedLength(column, min);
        var maxLength = GetDecimalEncodedLength(column, max);
        EnsureReusableBuffer(minLength, ref minBuffer, bufferPool);
        EnsureReusableBuffer(maxLength, ref maxBuffer, bufferPool);
        EncodeDecimal(column, min, minBuffer.Span[..minLength]);
        EncodeDecimal(column, max, maxBuffer.Span[..maxLength]);
        return new ColumnStatistics(minBuffer, minLength, maxBuffer, maxLength, nullCount, true);
    }

    static ColumnStatistics CreateNullableDecimal(Column column, ReadOnlySpan<decimal?> values)
    {
        if (!TryGetNullableDecimalMinMax(values, out var min, out var max, out var nullCount))
            return Empty(nullCount);

        var minValue = new byte[GetDecimalEncodedLength(column, min)];
        var maxValue = new byte[GetDecimalEncodedLength(column, max)];
        EncodeDecimal(column, min, minValue);
        EncodeDecimal(column, max, maxValue);
        return new ColumnStatistics(minValue, maxValue, nullCount, true);
    }

    static ColumnStatistics CreateNullableDecimal(Column column, ReadOnlySpan<decimal?> values,
        ref ParquetBuffer minBuffer, ref ParquetBuffer maxBuffer, IParquetBufferPool bufferPool)
    {
        if (!TryGetNullableDecimalMinMax(values, out var min, out var max, out var nullCount))
            return Empty(nullCount);

        var minLength = GetDecimalEncodedLength(column, min);
        var maxLength = GetDecimalEncodedLength(column, max);
        EnsureReusableBuffer(minLength, ref minBuffer, bufferPool);
        EnsureReusableBuffer(maxLength, ref maxBuffer, bufferPool);
        EncodeDecimal(column, min, minBuffer.Span[..minLength]);
        EncodeDecimal(column, max, maxBuffer.Span[..maxLength]);
        return new ColumnStatistics(minBuffer, minLength, maxBuffer, maxLength, nullCount, true);
    }

    static bool TryGetDecimalMinMax(ReadOnlySpan<decimal> values, out decimal min, out decimal max)
    {
        min = 0;
        max = 0;
        if (values.IsEmpty)
            return false;

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

    static bool TryGetNullableDecimalMinMax(ReadOnlySpan<decimal?> values, out decimal min, out decimal max,
        out long nullCount)
    {
        min = 0;
        max = 0;
        nullCount = 0;
        var hasValue = false;
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
        return hasValue;
    }

    static int GetDecimalEncodedLength(Column column, decimal value)
        => column.PhysicalType switch
        {
            ParquetPhysicalType.Int32 => sizeof(int),
            ParquetPhysicalType.Int64 => sizeof(long),
            ParquetPhysicalType.FixedLenByteArray => checked((int)column.Options.TypeLength),
            ParquetPhysicalType.ByteArray => ParquetDecimalConverter.GetByteCount(value, column),
            _ => throw new InvalidOperationException(
                $"Column '{column.Name}' has unsupported decimal physical type '{column.PhysicalType}'.")
        };

    static void EncodeDecimal(Column column, decimal value, Span<byte> destination)
    {
        switch (column.PhysicalType)
        {
            case ParquetPhysicalType.Int32:
                BinaryPrimitives.WriteInt32LittleEndian(destination, ParquetDecimalConverter.ToInt32(value, column));
                return;
            case ParquetPhysicalType.Int64:
                BinaryPrimitives.WriteInt64LittleEndian(destination, ParquetDecimalConverter.ToInt64(value, column));
                return;
            case ParquetPhysicalType.FixedLenByteArray:
                ParquetDecimalConverter.WriteFixedBigEndian(value, column, destination);
                return;
            case ParquetPhysicalType.ByteArray:
                _ = ParquetDecimalConverter.WriteBigEndian(value, column, destination);
                return;
            default:
                throw new InvalidOperationException(
                    $"Column '{column.Name}' has unsupported decimal physical type '{column.PhysicalType}'.");
        }
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
            if (CompareBinary(column, value, min) < 0)
                min = value;
            if (CompareBinary(column, value, max) > 0)
                max = value;
        }

        return new ColumnStatistics(min.ToArray(), max.ToArray(), nullCount, true);
    }

    static ColumnStatistics CreateByteArray(Column column, ReadOnlySpan<byte[]> values, long nullCount,
        ref ParquetBuffer minBuffer, ref ParquetBuffer maxBuffer, IParquetBufferPool bufferPool)
    {
        if (values.Length == 0)
            return Empty(nullCount);

        ReadOnlySpan<byte> min = values[0] ?? throw new InvalidOperationException($"Column '{column.Name}' does not support null values.");
        ReadOnlySpan<byte> max = min;
        for (var i = 1; i < values.Length; i++)
        {
            var value = values[i] ?? throw new InvalidOperationException($"Column '{column.Name}' does not support null values.");
            if (CompareBinary(column, value, min) < 0)
                min = value;
            if (CompareBinary(column, value, max) > 0)
                max = value;
        }

        CopyToReusableBuffer(min, ref minBuffer, bufferPool);
        CopyToReusableBuffer(max, ref maxBuffer, bufferPool);
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

            if (CompareBinary(column, value, min) < 0)
                min = value;
            if (CompareBinary(column, value, max!) > 0)
                max = value;
        }

        return min is null ? Empty(nullCount) : new ColumnStatistics(min.ToArray(), max!.ToArray(), nullCount, true);
    }

    static ColumnStatistics CreateOptionalByteArray(Column column, ReadOnlySpan<byte[]> values,
        ref ParquetBuffer minBuffer, ref ParquetBuffer maxBuffer, IParquetBufferPool bufferPool)
    {
        ReadOnlySpan<byte> min = default;
        ReadOnlySpan<byte> max = default;
        var hasValue = false;
        var nullCount = 0L;
        for (var i = 0; i < values.Length; i++)
        {
            var value = values[i];
            if (value is null)
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

            if (CompareBinary(column, value, min) < 0)
                min = value;
            if (CompareBinary(column, value, max) > 0)
                max = value;
        }

        if (!hasValue)
            return Empty(nullCount);

        CopyToReusableBuffer(min, ref minBuffer, bufferPool);
        CopyToReusableBuffer(max, ref maxBuffer, bufferPool);
        return new ColumnStatistics(minBuffer, min.Length, maxBuffer, max.Length, nullCount, true);
    }

    static ColumnStatistics CreateMemory(Column column, ReadOnlySpan<ReadOnlyMemory<byte>> values, long nullCount)
    {
        if (values.Length == 0)
            return Empty(nullCount);

        var min = values[0].Span;
        var max = min;
        for (var i = 1; i < values.Length; i++)
        {
            var value = values[i].Span;
            if (CompareBinary(column, value, min) < 0)
                min = value;
            if (CompareBinary(column, value, max) > 0)
                max = value;
        }

        return new ColumnStatistics(min.ToArray(), max.ToArray(), nullCount, true);
    }

    static ColumnStatistics CreateMemory(Column column, ReadOnlySpan<ReadOnlyMemory<byte>> values, long nullCount,
        ref ParquetBuffer minBuffer, ref ParquetBuffer maxBuffer, IParquetBufferPool bufferPool)
    {
        if (values.Length == 0)
            return Empty(nullCount);

        var min = values[0].Span;
        var max = min;
        for (var i = 1; i < values.Length; i++)
        {
            var value = values[i].Span;
            if (CompareBinary(column, value, min) < 0)
                min = value;
            if (CompareBinary(column, value, max) > 0)
                max = value;
        }

        CopyToReusableBuffer(min, ref minBuffer, bufferPool);
        CopyToReusableBuffer(max, ref maxBuffer, bufferPool);
        return new ColumnStatistics(minBuffer, min.Length, maxBuffer, max.Length, nullCount, true);
    }

    static ColumnStatistics CreateNullableMemory(Column column, ReadOnlySpan<ReadOnlyMemory<byte>?> values)
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

            if (CompareBinary(column, value, min) < 0)
                min = value.ToArray();
            if (CompareBinary(column, value, max!) > 0)
                max = value.ToArray();
        }

        return min is null ? Empty(nullCount) : new ColumnStatistics(min, max, nullCount, true);
    }

    static ColumnStatistics CreateNullableMemory(Column column, ReadOnlySpan<ReadOnlyMemory<byte>?> values,
        ref ParquetBuffer minBuffer, ref ParquetBuffer maxBuffer, IParquetBufferPool bufferPool)
    {
        ReadOnlySpan<byte> min = default;
        ReadOnlySpan<byte> max = default;
        var hasValue = false;
        var nullCount = 0L;
        for (var i = 0; i < values.Length; i++)
        {
            if (values[i] is not { } memory)
            {
                nullCount++;
                continue;
            }

            var value = memory.Span;
            if (!hasValue)
            {
                min = value;
                max = value;
                hasValue = true;
                continue;
            }

            if (CompareBinary(column, value, min) < 0)
                min = value;
            if (CompareBinary(column, value, max) > 0)
                max = value;
        }

        if (!hasValue)
            return Empty(nullCount);

        CopyToReusableBuffer(min, ref minBuffer, bufferPool);
        CopyToReusableBuffer(max, ref maxBuffer, bufferPool);
        return new ColumnStatistics(minBuffer, min.Length, maxBuffer, max.Length, nullCount, true);
    }

    static bool TryGetFloatMinMax(ReadOnlySpan<float> values, out float min, out float max, out long nanCount)
    {
        min = 0;
        max = 0;
        nanCount = 0;
        if (values.Length == 0)
            return false;
        if (Vector.IsHardwareAccelerated && values.Length >= Vector<float>.Count)
            return TryGetFloatMinMaxVectorized(values, out min, out max, out nanCount);

        var first = values[0];
        if (float.IsNaN(first))
            return TryGetFloatMinMaxScalar(values, out min, out max, out nanCount);

        min = first;
        max = first;

        for (var i = 1; i < values.Length; i++)
        {
            var value = values[i];
            if (float.IsNaN(value))
            {
                nanCount++;
                continue;
            }

            if (value < min)
                min = value;
            if (value > max)
                max = value;
        }

        CanonicalizeSignedZeroBounds(values, ref min, ref max);
        return true;
    }

    internal static bool TryGetDoubleMinMax(ReadOnlySpan<double> values, out double min, out double max,
        out long nanCount)
    {
        min = 0;
        max = 0;
        nanCount = 0;
        if (values.Length == 0)
            return false;
        if (Vector.IsHardwareAccelerated && values.Length >= Vector<double>.Count)
            return TryGetDoubleMinMaxVectorized(values, out min, out max, out nanCount);

        var first = values[0];
        if (double.IsNaN(first))
            return TryGetDoubleMinMaxScalar(values, out min, out max, out nanCount);

        min = first;
        max = first;

        for (var i = 1; i < values.Length; i++)
        {
            var value = values[i];
            if (double.IsNaN(value))
            {
                nanCount++;
                continue;
            }

            if (value < min)
                min = value;
            if (value > max)
                max = value;
        }

        CanonicalizeSignedZeroBounds(values, ref min, ref max);
        return true;
    }

    static bool TryGetFloatMinMaxVectorized(ReadOnlySpan<float> values, out float min, out float max,
        out long nanCount)
    {
        nanCount = 0;
        var width = Vector<float>.Count;
        var first = new Vector<float>(values);
        if (!Vector.EqualsAll(first, first))
            return TryGetFloatMinMaxScalar(values, out min, out max, out nanCount);

        var minVector = first;
        var maxVector = first;
        var i = width;
        for (; i <= values.Length - width; i += width)
        {
            var current = new Vector<float>(values[i..]);
            if (!Vector.EqualsAll(current, current))
                return TryGetFloatMinMaxScalar(values, out min, out max, out nanCount);

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
                return TryGetFloatMinMaxScalar(values, out min, out max, out nanCount);
            if (value < min)
                min = value;
            if (value > max)
                max = value;
        }

        CanonicalizeSignedZeroBounds(values, ref min, ref max);
        return true;
    }

    static bool TryGetDoubleMinMaxVectorized(ReadOnlySpan<double> values, out double min, out double max,
        out long nanCount)
    {
        nanCount = 0;
        var width = Vector<double>.Count;
        var first = new Vector<double>(values);
        if (!Vector.EqualsAll(first, first))
            return TryGetDoubleMinMaxScalar(values, out min, out max, out nanCount);

        var minVector = first;
        var maxVector = first;
        var i = width;
        for (; i <= values.Length - width; i += width)
        {
            var current = new Vector<double>(values[i..]);
            if (!Vector.EqualsAll(current, current))
                return TryGetDoubleMinMaxScalar(values, out min, out max, out nanCount);

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
                return TryGetDoubleMinMaxScalar(values, out min, out max, out nanCount);
            if (value < min)
                min = value;
            if (value > max)
                max = value;
        }

        CanonicalizeSignedZeroBounds(values, ref min, ref max);
        return true;
    }

    static bool TryGetFloatMinMaxScalar(ReadOnlySpan<float> values, out float min, out float max,
        out long nanCount)
    {
        min = 0;
        max = 0;
        nanCount = 0;
        var hasValue = false;
        for (var i = 0; i < values.Length; i++)
        {
            var value = values[i];
            if (float.IsNaN(value))
            {
                nanCount++;
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

        if (hasValue)
            CanonicalizeSignedZeroBounds(values, ref min, ref max);
        return hasValue;
    }

    static bool TryGetNullableFloatMinMax(ReadOnlySpan<float?> values, out float min, out float max,
        out long nullCount, out long nanCount)
    {
        min = 0;
        max = 0;
        nullCount = 0;
        nanCount = 0;
        var hasValue = false;
        for (var i = 0; i < values.Length; i++)
        {
            if (values[i] is not { } value)
            {
                nullCount++;
                continue;
            }

            if (float.IsNaN(value))
            {
                nanCount++;
                continue;
            }

            if (!hasValue)
            {
                min = value;
                max = value;
                hasValue = true;
                continue;
            }

            if (IsLessThan(value, min))
                min = value;
            if (IsGreaterThan(value, max))
                max = value;
        }

        return hasValue;
    }

    static bool IsLessThan(float value, float other)
        => value < other || value == 0 && other == 0 &&
            BitConverter.SingleToInt32Bits(value) < BitConverter.SingleToInt32Bits(other);

    static bool IsGreaterThan(float value, float other)
        => value > other || value == 0 && other == 0 &&
            BitConverter.SingleToInt32Bits(value) > BitConverter.SingleToInt32Bits(other);

    internal static bool IsLessThan(double value, double other)
        => value < other || value == 0 && other == 0 &&
            BitConverter.DoubleToInt64Bits(value) < BitConverter.DoubleToInt64Bits(other);

    internal static bool IsGreaterThan(double value, double other)
        => value > other || value == 0 && other == 0 &&
            BitConverter.DoubleToInt64Bits(value) > BitConverter.DoubleToInt64Bits(other);

    static void CanonicalizeSignedZeroBounds(ReadOnlySpan<float> values, ref float min, ref float max)
    {
        var canonicalizeMin = min == 0;
        var canonicalizeMax = max == 0;
        if (!canonicalizeMin && !canonicalizeMax)
            return;

        var hasNegativeZero = false;
        var hasPositiveZero = false;
        for (var i = 0; i < values.Length; i++)
        {
            var value = values[i];
            if (value != 0)
                continue;

            if (BitConverter.SingleToInt32Bits(value) < 0)
                hasNegativeZero = true;
            else
                hasPositiveZero = true;

            if ((!canonicalizeMin || hasNegativeZero) && (!canonicalizeMax || hasPositiveZero))
                break;
        }

        if (canonicalizeMin)
            min = hasNegativeZero ? -0.0f : +0.0f;
        if (canonicalizeMax)
            max = hasPositiveZero ? +0.0f : -0.0f;
    }

    static void CanonicalizeSignedZeroBounds(ReadOnlySpan<double> values, ref double min, ref double max)
    {
        var canonicalizeMin = min == 0;
        var canonicalizeMax = max == 0;
        if (!canonicalizeMin && !canonicalizeMax)
            return;

        var hasNegativeZero = false;
        var hasPositiveZero = false;
        for (var i = 0; i < values.Length; i++)
        {
            var value = values[i];
            if (value != 0)
                continue;

            if (BitConverter.DoubleToInt64Bits(value) < 0)
                hasNegativeZero = true;
            else
                hasPositiveZero = true;

            if ((!canonicalizeMin || hasNegativeZero) && (!canonicalizeMax || hasPositiveZero))
                break;
        }

        if (canonicalizeMin)
            min = hasNegativeZero ? -0.0d : +0.0d;
        if (canonicalizeMax)
            max = hasPositiveZero ? +0.0d : -0.0d;
    }

    static bool TryGetDoubleMinMaxScalar(ReadOnlySpan<double> values, out double min, out double max,
        out long nanCount)
    {
        min = 0;
        max = 0;
        nanCount = 0;
        var hasValue = false;
        for (var i = 0; i < values.Length; i++)
        {
            var value = values[i];
            if (double.IsNaN(value))
            {
                nanCount++;
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

        if (hasValue)
            CanonicalizeSignedZeroBounds(values, ref min, ref max);
        return hasValue;
    }

    static bool TryGetNullableDoubleMinMax(ReadOnlySpan<double?> values, out double min, out double max,
        out long nullCount, out long nanCount)
    {
        min = 0;
        max = 0;
        nullCount = 0;
        nanCount = 0;
        var hasValue = false;
        for (var i = 0; i < values.Length; i++)
        {
            if (values[i] is not { } value)
            {
                nullCount++;
                continue;
            }

            if (double.IsNaN(value))
            {
                nanCount++;
                continue;
            }

            if (!hasValue)
            {
                min = value;
                max = value;
                hasValue = true;
                continue;
            }

            if (IsLessThan(value, min))
                min = value;
            if (IsGreaterThan(value, max))
                max = value;
        }

        return hasValue;
    }

    internal static ColumnStatistics FromInt32(int min, int max, long nullCount)
        => new(ColumnStatisticsValueKind.Int32, min, max, nullCount, true);

    static ColumnStatistics FromUInt32(uint min, uint max, long nullCount)
        => new(ColumnStatisticsValueKind.UInt32, min, max, nullCount, true);

    internal static ColumnStatistics FromInt64(long min, long max, long nullCount)
        => new(ColumnStatisticsValueKind.Int64, min, max, nullCount, true);

    static ColumnStatistics FromUInt64(ulong min, ulong max, long nullCount)
        => new(ColumnStatisticsValueKind.UInt64, unchecked((long)min), unchecked((long)max), nullCount, true);

    static ColumnStatistics FromFloat(float min, float max, long nullCount, long nanCount)
        => new(ColumnStatisticsValueKind.Float, BitConverter.SingleToInt32Bits(min),
            BitConverter.SingleToInt32Bits(max), nullCount, true, nanCount);

    static ColumnStatistics FromDouble(double min, double max, long nullCount, long nanCount)
        => new(ColumnStatisticsValueKind.Double, BitConverter.DoubleToInt64Bits(min),
            BitConverter.DoubleToInt64Bits(max), nullCount, true, nanCount);

    internal static ColumnStatistics FromDoubleAccumulation(double min, double max, long nullCount,
        long nanCount, bool hasValue)
        => hasValue ? FromDouble(min, max, nullCount, nanCount) : EmptyFloating(nullCount, nanCount);

    static int CompareBinary(Column column, ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
        => column.LogicalType is LogicalType.Decimal
            ? CompareDecimalBytes(left, right)
            : left.SequenceCompareTo(right);

    static int CompareDecimalBytes(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        if (left.IsEmpty)
            return right.IsEmpty ? 0 : CompareZeroToDecimal(right);
        if (right.IsEmpty)
            return -CompareZeroToDecimal(left);

        var leftNegative = (left[0] & 0x80) != 0;
        var rightNegative = (right[0] & 0x80) != 0;
        if (leftNegative != rightNegative)
            return leftNegative ? -1 : 1;

        left = TrimDecimalSignExtension(left, leftNegative);
        right = TrimDecimalSignExtension(right, rightNegative);
        var lengthComparison = left.Length.CompareTo(right.Length);
        if (lengthComparison != 0)
            return leftNegative ? -lengthComparison : lengthComparison;

        return left.SequenceCompareTo(right);
    }

    static int CompareZeroToDecimal(ReadOnlySpan<byte> value)
    {
        if ((value[0] & 0x80) != 0)
            return 1;
        for (var i = 0; i < value.Length; i++)
            if (value[i] != 0)
                return -1;
        return 0;
    }

    static ReadOnlySpan<byte> TrimDecimalSignExtension(ReadOnlySpan<byte> value, bool negative)
    {
        var extension = negative ? byte.MaxValue : (byte)0;
        while (value.Length > 1 && value[0] == extension && ((value[1] & 0x80) != 0) == negative)
            value = value[1..];
        return value;
    }

    static void CopyToReusableBuffer(ReadOnlySpan<byte> source, ref ParquetBuffer buffer,
        IParquetBufferPool bufferPool)
    {
        EnsureReusableBuffer(source.Length, ref buffer, bufferPool);

        if (!source.IsEmpty)
            source.CopyTo(buffer.Span);
    }

    static void EnsureReusableBuffer(int length, ref ParquetBuffer buffer, IParquetBufferPool bufferPool)
    {
        if (buffer.Length >= length)
            return;

        buffer.Dispose();
        buffer = length == 0 ? default : bufferPool.Rent(checked((uint)length));
    }

    bool HasNativeBinaryValues
        => ValueKind == ColumnStatisticsValueKind.Binary && MinValue is null;

    static ReadOnlySpan<byte> GetValueSpan(ParquetBuffer buffer, int length)
        => length == 0 ? [] : buffer.Span[..length];

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
        long _nanCount;
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
            _nanCount = 0;
            _minBytes = null;
            _maxBytes = null;
        }

        internal void AddNode(object? value)
        {
            if (value is null)
                return;

            if ((_column.PhysicalType is ParquetPhysicalType.ByteArray or ParquetPhysicalType.FixedLenByteArray
                    or ParquetPhysicalType.Int96) && value is byte[])
            {
                AddLeaf(value);
                return;
            }

            if (value is Array array)
            {
                for (var i = 0; i < array.Length; i++)
                    AddNode(array.GetValue(i));
                return;
            }

            AddLeaf(value);
        }

        internal ColumnStatistics ToStatistics(long nullCount)
        {
            if (!_hasValue)
                return _column.PhysicalType is ParquetPhysicalType.Float or ParquetPhysicalType.Double
                    ? EmptyFloating(nullCount, _nanCount)
                    : Empty(nullCount);

            return _column.PhysicalType switch
            {
                ParquetPhysicalType.Boolean => new ColumnStatistics(ColumnStatisticsValueKind.Boolean,
                    _minBool ? 1 : 0, _maxBool ? 1 : 0, nullCount, true),
                ParquetPhysicalType.Int32 when _column.LogicalType is LogicalType.Int { IsSigned: false }
                    => FromUInt32(_minUInt32, _maxUInt32, nullCount),
                ParquetPhysicalType.Int32 => FromInt32(_minInt32, _maxInt32, nullCount),
                ParquetPhysicalType.Int64 when _column.LogicalType is LogicalType.Int { IsSigned: false }
                    => FromUInt64(_minUInt64, _maxUInt64, nullCount),
                ParquetPhysicalType.Int64 => FromInt64(_minInt64, _maxInt64, nullCount),
                ParquetPhysicalType.Float => FromFloat(_minFloat, _maxFloat, nullCount, _nanCount),
                ParquetPhysicalType.Double => FromDouble(_minDouble, _maxDouble, nullCount, _nanCount),
                ParquetPhysicalType.ByteArray or ParquetPhysicalType.FixedLenByteArray or ParquetPhysicalType.Int96
                    => _minBytes is null ? Empty(nullCount) : new ColumnStatistics(_minBytes, _maxBytes, nullCount, true),
                _ => Empty(nullCount)
            };
        }

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
                DateTimeOffset dateTimeOffset => ToUnixTimeOffsetForStatistics(dateTimeOffset),
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
            {
                _nanCount++;
                return;
            }

            if (!_hasValue)
            {
                _minFloat = value;
                _maxFloat = value;
                _hasValue = true;
                return;
            }

            if (IsLessThan(value, _minFloat))
                _minFloat = value;
            if (IsGreaterThan(value, _maxFloat))
                _maxFloat = value;
        }

        void AddDouble(double value)
        {
            if (double.IsNaN(value))
            {
                _nanCount++;
                return;
            }

            if (!_hasValue)
            {
                _minDouble = value;
                _maxDouble = value;
                _hasValue = true;
                return;
            }

            if (IsLessThan(value, _minDouble))
                _minDouble = value;
            if (IsGreaterThan(value, _maxDouble))
                _maxDouble = value;
        }

        void AddBinaryLeaf(object value)
        {
            var bytes = value switch
            {
                byte[] array => array,
                ReadOnlyMemory<byte> memory => memory.ToArray(),
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

            if (CompareBinary(_column, bytes, _minBytes) < 0)
                _minBytes = bytes.ToArray();
            if (CompareBinary(_column, bytes, _maxBytes) > 0)
                _maxBytes = bytes.ToArray();
        }

        long ToUnixTimeForStatistics(DateTime value)
        {
            var expectedKind = _column.LogicalType is LogicalType.Timestamp { IsAdjustedToUtc: false }
                ? DateTimeKind.Unspecified
                : DateTimeKind.Utc;
            if (value.Kind != expectedKind)
                throw new InvalidOperationException(
                    $"DateTime values must have kind '{expectedKind}', got '{value.Kind}'.");

            return ToUnixTimeForStatistics(value.Ticks);
        }

        long ToUnixTimeForStatistics(long ticks)
        {
            var unit = _column.LogicalType is LogicalType.Timestamp timestamp ? timestamp.Unit : TimeUnit.Micros;
            var deltaTicks = ticks - DateTime.UnixEpoch.Ticks;
            return unit switch
            {
                TimeUnit.Millis => TimestampConversion.DivideFloor(deltaTicks, TimeSpan.TicksPerMillisecond),
                TimeUnit.Micros => TimestampConversion.DivideFloor(deltaTicks, 10),
                TimeUnit.Nanos => checked(deltaTicks * 100),
                _ => throw new ArgumentOutOfRangeException(nameof(unit), unit, "Time unit must be a defined TimeUnit value.")
            };
        }

        long ToUnixTimeOffsetForStatistics(DateTimeOffset value)
        {
            if (_column.LogicalType is LogicalType.Timestamp { IsAdjustedToUtc: false })
                throw new InvalidOperationException(
                    "DateTimeOffset values require adjusted-to-UTC timestamp semantics.");

            return ToUnixTimeForStatistics(value.UtcDateTime.Ticks);
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
