using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Plank.Schema;

namespace Plank.Writing.Encoding;

static class PlainEncoding
{
    internal static void WriteValues<T>(Column column, ReadOnlySpan<T> values, ref BufferWriter writer)
        where T : notnull
    {
        switch (column.PhysicalType)
        {
            case ParquetPhysicalType.Boolean:
                WriteBooleanValues(column, values, ref writer);
                return;
            case ParquetPhysicalType.Int32:
                WriteInt32Values(column, values, ref writer);
                return;
            case ParquetPhysicalType.Int64:
                WriteInt64Values(column, values, ref writer);
                return;
            case ParquetPhysicalType.Float:
                WriteFloatValues(column, values, ref writer);
                return;
            case ParquetPhysicalType.Double:
                WriteDoubleValues(column, values, ref writer);
                return;
            case ParquetPhysicalType.ByteArray:
                WriteByteArrayValues(column, values, ref writer);
                return;
            case ParquetPhysicalType.FixedLenByteArray:
                WriteFixedLengthByteArrayValues(column, values, ref writer);
                return;
            case ParquetPhysicalType.Int96:
                WriteInt96Values(column, values, ref writer);
                return;
            default:
                throw new NotSupportedException(
                    $"Physical type '{column.PhysicalType}' is not supported by plain encoding.");
        }
    }

    static void WriteBooleanValues<T>(Column column, ReadOnlySpan<T> values, ref BufferWriter writer)
        where T : notnull
    {
        if (typeof(T) != typeof(bool))
            throw new InvalidOperationException(
                $"Column '{column.Name}' expects '{ParquetPhysicalType.Boolean}' values, but got '{typeof(T)}'.");

        var booleanValues = Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<bool>>(ref values);
        var byteCount = (booleanValues.Length + 7) >> 3;
        if (byteCount == 0)
            return;

        EncodingPrimitives.PackBooleans(booleanValues, writer.GetSpan(byteCount));
        writer.Advance(byteCount);
    }

    internal static ColumnStatistics WriteBooleanPageWithStatistics(ReadOnlySpan<bool> values,
        ref BufferWriter writer)
    {
        var byteCount = (values.Length + 7) >> 3;
        if (byteCount == 0)
            return ColumnStatistics.Empty(0);

        var destination = writer.GetSpan(byteCount)[..byteCount];
        EncodingPrimitives.PackBooleans(values, destination);
        var anyTrue = false;
        var allTrue = true;
        var fullByteCount = values.Length >> 3;
        for (var i = 0; i < fullByteCount; i++)
        {
            anyTrue |= destination[i] != 0;
            allTrue &= destination[i] == byte.MaxValue;
        }

        var remainingBits = values.Length & 7;
        if (remainingBits != 0)
        {
            var mask = (1 << remainingBits) - 1;
            var last = destination[^1] & mask;
            anyTrue |= last != 0;
            allTrue &= last == mask;
        }

        writer.Advance(byteCount);
        return ColumnStatistics.FromBoolean(allTrue, anyTrue, 0);
    }

    static void WriteInt32Values<T>(Column column, ReadOnlySpan<T> values, ref BufferWriter writer)
        where T : notnull
    {
        var byteCount = checked(values.Length * sizeof(int));
        if (byteCount == 0)
            return;

        var destination = writer.GetSpan(byteCount);
        if (typeof(T) == typeof(int))
        {
            var intValues = Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<int>>(ref values);
            if (BitConverter.IsLittleEndian)
                MemoryMarshal.AsBytes(intValues).CopyTo(destination);
            else
                for (var i = 0; i < intValues.Length; i++)
                    BinaryPrimitives.WriteInt32LittleEndian(destination[(i * sizeof(int))..], intValues[i]);
            writer.Advance(byteCount);
            return;
        }

        if (typeof(T) == typeof(byte))
        {
            var byteValues = Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<byte>>(ref values);
            if (BitConverter.IsLittleEndian)
                WriteByteValuesAsUInt32(byteValues, destination);
            else
                for (var i = 0; i < byteValues.Length; i++)
                    BinaryPrimitives.WriteInt32LittleEndian(destination[(i * sizeof(int))..], byteValues[i]);
            writer.Advance(byteCount);
            return;
        }

        if (typeof(T) == typeof(ushort))
        {
            var ushortValues = Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<ushort>>(ref values);
            if (BitConverter.IsLittleEndian)
                WriteUInt16ValuesAsUInt32(ushortValues, destination);
            else
                for (var i = 0; i < ushortValues.Length; i++)
                    BinaryPrimitives.WriteInt32LittleEndian(destination[(i * sizeof(int))..], ushortValues[i]);
            writer.Advance(byteCount);
            return;
        }

        if (typeof(T) == typeof(uint))
        {
            var uintValues = Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<uint>>(ref values);
            if (BitConverter.IsLittleEndian)
                MemoryMarshal.AsBytes(uintValues).CopyTo(destination);
            else
                for (var i = 0; i < uintValues.Length; i++)
                    BinaryPrimitives.WriteUInt32LittleEndian(destination[(i * sizeof(int))..], uintValues[i]);
            writer.Advance(byteCount);
            return;
        }

        if (typeof(T) == typeof(decimal))
        {
            var decimalValues = Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<decimal>>(ref values);
            for (var i = 0; i < decimalValues.Length; i++)
                BinaryPrimitives.WriteInt32LittleEndian(destination[(i * sizeof(int))..],
                    ParquetDecimalConverter.ToInt32(decimalValues[i], column));
            writer.Advance(byteCount);
            return;
        }

        throw new InvalidOperationException(
            $"Column '{column.Name}' expects '{ParquetPhysicalType.Int32}' values, but got '{typeof(T)}'.");
    }

    static void WriteByteValuesAsUInt32(ReadOnlySpan<byte> values, Span<byte> destination)
    {
        ref var source = ref MemoryMarshal.GetReference(values);
        ref var destinationValues = ref Unsafe.As<byte, uint>(ref MemoryMarshal.GetReference(destination));
        var length = (nuint)values.Length;
        nuint valueIndex = 0;

        if (Avx512F.IsSupported)
        {
            var vectorCount = (nuint)Vector128<byte>.Count;
            var blockCount = vectorCount * 4;
            for (; length - valueIndex >= blockCount; valueIndex += blockCount)
            {
                Avx512F.ConvertToVector512Int32(Vector128.LoadUnsafe(ref source, valueIndex))
                    .AsUInt32().StoreUnsafe(ref destinationValues, valueIndex);
                Avx512F.ConvertToVector512Int32(Vector128.LoadUnsafe(ref source, valueIndex + vectorCount))
                    .AsUInt32().StoreUnsafe(ref destinationValues, valueIndex + vectorCount);
                Avx512F.ConvertToVector512Int32(Vector128.LoadUnsafe(ref source, valueIndex + vectorCount * 2))
                    .AsUInt32().StoreUnsafe(ref destinationValues, valueIndex + vectorCount * 2);
                Avx512F.ConvertToVector512Int32(Vector128.LoadUnsafe(ref source, valueIndex + vectorCount * 3))
                    .AsUInt32().StoreUnsafe(ref destinationValues, valueIndex + vectorCount * 3);
            }

            for (; length - valueIndex >= vectorCount; valueIndex += vectorCount)
                Avx512F.ConvertToVector512Int32(Vector128.LoadUnsafe(ref source, valueIndex))
                    .AsUInt32().StoreUnsafe(ref destinationValues, valueIndex);
        }
        else if (Vector256.IsHardwareAccelerated)
        {
            var vectorCount = (nuint)Vector256<byte>.Count;
            var widenedVectorCount = (nuint)Vector256<uint>.Count;
            for (; length - valueIndex >= vectorCount; valueIndex += vectorCount)
            {
                var sourceVector = Vector256.LoadUnsafe(ref source, valueIndex);
                var halves = Vector256.Widen(sourceVector);
                var lowerQuarters = Vector256.Widen(halves.Lower);
                var upperQuarters = Vector256.Widen(halves.Upper);
                lowerQuarters.Lower.StoreUnsafe(ref destinationValues, valueIndex);
                lowerQuarters.Upper.StoreUnsafe(ref destinationValues, valueIndex + widenedVectorCount);
                upperQuarters.Lower.StoreUnsafe(ref destinationValues, valueIndex + widenedVectorCount * 2);
                upperQuarters.Upper.StoreUnsafe(ref destinationValues, valueIndex + widenedVectorCount * 3);
            }
        }

        for (; valueIndex < length; valueIndex++)
            Unsafe.Add(ref destinationValues, valueIndex) = Unsafe.Add(ref source, valueIndex);
    }

    static void WriteUInt16ValuesAsUInt32(ReadOnlySpan<ushort> values, Span<byte> destination)
    {
        ref var source = ref MemoryMarshal.GetReference(values);
        ref var destinationValues = ref Unsafe.As<byte, uint>(ref MemoryMarshal.GetReference(destination));
        var length = (nuint)values.Length;
        nuint valueIndex = 0;

        if (Avx512F.IsSupported)
        {
            var vectorCount = (nuint)Vector256<ushort>.Count;
            var blockCount = vectorCount * 4;
            for (; length - valueIndex >= blockCount; valueIndex += blockCount)
            {
                Avx512F.ConvertToVector512Int32(Vector256.LoadUnsafe(ref source, valueIndex))
                    .AsUInt32().StoreUnsafe(ref destinationValues, valueIndex);
                Avx512F.ConvertToVector512Int32(Vector256.LoadUnsafe(ref source, valueIndex + vectorCount))
                    .AsUInt32().StoreUnsafe(ref destinationValues, valueIndex + vectorCount);
                Avx512F.ConvertToVector512Int32(Vector256.LoadUnsafe(ref source, valueIndex + vectorCount * 2))
                    .AsUInt32().StoreUnsafe(ref destinationValues, valueIndex + vectorCount * 2);
                Avx512F.ConvertToVector512Int32(Vector256.LoadUnsafe(ref source, valueIndex + vectorCount * 3))
                    .AsUInt32().StoreUnsafe(ref destinationValues, valueIndex + vectorCount * 3);
            }

            for (; length - valueIndex >= vectorCount; valueIndex += vectorCount)
                Avx512F.ConvertToVector512Int32(Vector256.LoadUnsafe(ref source, valueIndex))
                    .AsUInt32().StoreUnsafe(ref destinationValues, valueIndex);
        }
        else if (Vector256.IsHardwareAccelerated)
        {
            var vectorCount = (nuint)Vector256<ushort>.Count;
            for (; length - valueIndex >= vectorCount; valueIndex += vectorCount)
            {
                var halves = Vector256.Widen(Vector256.LoadUnsafe(ref source, valueIndex));
                halves.Lower.StoreUnsafe(ref destinationValues, valueIndex);
                halves.Upper.StoreUnsafe(ref destinationValues, valueIndex + (nuint)Vector256<uint>.Count);
            }
        }

        for (; valueIndex < length; valueIndex++)
            Unsafe.Add(ref destinationValues, valueIndex) = Unsafe.Add(ref source, valueIndex);
    }

    static void WriteInt64Values<T>(Column column, ReadOnlySpan<T> values, ref BufferWriter writer)
        where T : notnull
    {
        var byteCount = checked(values.Length * sizeof(long));
        if (byteCount == 0)
            return;

        var destination = writer.GetSpan(byteCount);
        if (typeof(T) == typeof(long))
        {
            var longValues = Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<long>>(ref values);
            if (BitConverter.IsLittleEndian)
                MemoryMarshal.AsBytes(longValues).CopyTo(destination);
            else
                for (var i = 0; i < longValues.Length; i++)
                    BinaryPrimitives.WriteInt64LittleEndian(destination[(i * sizeof(long))..], longValues[i]);
            writer.Advance(byteCount);
            return;
        }

        if (typeof(T) == typeof(ulong))
        {
            var ulongValues = Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<ulong>>(ref values);
            if (BitConverter.IsLittleEndian)
                MemoryMarshal.AsBytes(ulongValues).CopyTo(destination);
            else
                for (var i = 0; i < ulongValues.Length; i++)
                    BinaryPrimitives.WriteUInt64LittleEndian(destination[(i * sizeof(long))..], ulongValues[i]);
            writer.Advance(byteCount);
            return;
        }

        if (typeof(T) == typeof(decimal))
        {
            var decimalValues = Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<decimal>>(ref values);
            for (var i = 0; i < decimalValues.Length; i++)
                BinaryPrimitives.WriteInt64LittleEndian(destination[(i * sizeof(long))..],
                    ParquetDecimalConverter.ToInt64(decimalValues[i], column));
            writer.Advance(byteCount);
            return;
        }

        throw new InvalidOperationException(
            $"Column '{column.Name}' expects '{ParquetPhysicalType.Int64}' values, but got '{typeof(T)}'.");
    }

    static void WriteFloatValues<T>(Column column, ReadOnlySpan<T> values, ref BufferWriter writer)
        where T : notnull
    {
        if (typeof(T) != typeof(float))
            throw new InvalidOperationException(
                $"Column '{column.Name}' expects '{ParquetPhysicalType.Float}' values, but got '{typeof(T)}'.");

        var floatValues = Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<float>>(ref values);
        var byteCount = checked(floatValues.Length * sizeof(float));
        if (byteCount == 0)
            return;

        var destination = writer.GetSpan(byteCount);
        if (BitConverter.IsLittleEndian)
            MemoryMarshal.AsBytes(floatValues).CopyTo(destination);
        else
            for (var i = 0; i < floatValues.Length; i++)
                BinaryPrimitives.WriteInt32LittleEndian(destination[(i * sizeof(float))..],
                    BitConverter.SingleToInt32Bits(floatValues[i]));

        writer.Advance(byteCount);
    }

    static void WriteDoubleValues<T>(Column column, ReadOnlySpan<T> values, ref BufferWriter writer)
        where T : notnull
    {
        if (typeof(T) != typeof(double))
            throw new InvalidOperationException(
                $"Column '{column.Name}' expects '{ParquetPhysicalType.Double}' values, but got '{typeof(T)}'.");

        var doubleValues = Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<double>>(ref values);
        var byteCount = checked(doubleValues.Length * sizeof(double));
        if (byteCount == 0)
            return;

        var destination = writer.GetSpan(byteCount);
        if (BitConverter.IsLittleEndian)
            MemoryMarshal.AsBytes(doubleValues).CopyTo(destination);
        else
            for (var i = 0; i < doubleValues.Length; i++)
                BinaryPrimitives.WriteInt64LittleEndian(destination[(i * sizeof(double))..],
                    BitConverter.DoubleToInt64Bits(doubleValues[i]));

        writer.Advance(byteCount);
    }

    internal static ColumnStatistics WriteInt32PageWithStatistics(ReadOnlySpan<int> values,
        ref BufferWriter writer)
    {
        var byteCount = checked(values.Length * sizeof(int));
        if (byteCount == 0)
            return ColumnStatistics.Empty(0);

        var destinationBytes = writer.GetSpan(byteCount)[..byteCount];
        int min;
        int max;
        if (BitConverter.IsLittleEndian)
        {
            MinMaxScan.CopyAndCompute(values, MemoryMarshal.Cast<byte, int>(destinationBytes), out min, out max);
        }
        else
        {
            for (var i = 0; i < values.Length; i++)
                BinaryPrimitives.WriteInt32LittleEndian(destinationBytes[(i * sizeof(int))..], values[i]);
            MinMaxScan.Compute(values, out min, out max);
        }

        writer.Advance(byteCount);
        return ColumnStatistics.FromInt32(min, max, 0);
    }

    internal static ColumnStatistics WriteAllPresentOptionalInt32PageWithStatistics(
        ReadOnlySpan<int?> values, ref BufferWriter writer)
    {
        var byteCount = checked(values.Length * sizeof(int));
        if (byteCount == 0)
            return ColumnStatistics.Empty(0);

        var destination = MemoryMarshal.Cast<byte, int>(writer.GetSpan(byteCount)[..byteCount]);
        if (values.Length >= Vector256<int>.Count)
        {
            ref var nullableSource = ref MemoryMarshal.GetReference(values);
            ref var source = ref Unsafe.As<int?, int>(ref nullableSource);
            ref var target = ref MemoryMarshal.GetReference(destination);
            var valueIndexes = Vector256.Create(1, 3, 5, 7, 1, 3, 5, 7);
            var valueIndex = 0;
            for (; values.Length - valueIndex >= 8; valueIndex += 8)
            {
                var firstRows = Vector256.LoadUnsafe(ref source, checked((nuint)valueIndex * 2));
                var secondRows = Vector256.LoadUnsafe(ref source, checked((nuint)valueIndex * 2 + 8));
                var firstValues = Avx2.PermuteVar8x32(firstRows, valueIndexes);
                var secondValues = Avx2.PermuteVar8x32(secondRows, valueIndexes);
                Avx2.Permute2x128(firstValues, secondValues, 0x20)
                    .StoreUnsafe(ref target, checked((nuint)valueIndex));
            }

            for (; valueIndex < values.Length; valueIndex++)
                destination[valueIndex] = values[valueIndex]!.Value;
        }
        else
        {
            for (var i = 0; i < values.Length; i++)
                destination[i] = values[i]!.Value;
        }

        MinMaxScan.Compute(destination, out var min, out var max);
        writer.Advance(byteCount);
        return ColumnStatistics.FromInt32(min, max, 0);
    }

    internal static ColumnStatistics WriteInt64PageWithStatistics(ReadOnlySpan<long> values,
        ref BufferWriter writer)
    {
        var byteCount = checked(values.Length * sizeof(long));
        if (byteCount == 0)
            return ColumnStatistics.Empty(0);

        var destinationBytes = writer.GetSpan(byteCount)[..byteCount];
        long min;
        long max;
        if (BitConverter.IsLittleEndian)
        {
            MinMaxScan.CopyAndCompute(values, MemoryMarshal.Cast<byte, long>(destinationBytes), out min, out max);
        }
        else
        {
            for (var i = 0; i < values.Length; i++)
                BinaryPrimitives.WriteInt64LittleEndian(destinationBytes[(i * sizeof(long))..], values[i]);
            MinMaxScan.Compute(values, out min, out max);
        }

        writer.Advance(byteCount);
        return ColumnStatistics.FromInt64(min, max, 0);
    }

    internal static ColumnStatistics WriteFloatPageWithStatistics(ReadOnlySpan<float> values,
        ref BufferWriter writer)
    {
        var byteCount = checked(values.Length * sizeof(float));
        if (byteCount == 0)
            return ColumnStatistics.FromFloatAccumulation(0, 0, 0, 0, false);

        var destinationBytes = writer.GetSpan(byteCount)[..byteCount];
        if (BitConverter.IsLittleEndian)
            MemoryMarshal.AsBytes(values).CopyTo(destinationBytes);
        else
            for (var i = 0; i < values.Length; i++)
                BinaryPrimitives.WriteInt32LittleEndian(destinationBytes[(i * sizeof(float))..],
                    BitConverter.SingleToInt32Bits(values[i]));

        var hasValue = ColumnStatistics.TryGetFloatMinMax(values, out var min, out var max, out var nanCount);
        writer.Advance(byteCount);
        return ColumnStatistics.FromFloatAccumulation(min, max, 0, nanCount, hasValue);
    }

    internal static ColumnStatistics WriteDoublePageWithStatistics(ReadOnlySpan<double> values,
        ref BufferWriter writer)
    {
        var byteCount = checked(values.Length * sizeof(double));
        if (byteCount == 0)
            return ColumnStatistics.FromDoubleAccumulation(0, 0, 0, 0, false);

        var destinationBytes = writer.GetSpan(byteCount)[..byteCount];
        bool hasValue;
        double min;
        double max;
        long nanCount;
        if (BitConverter.IsLittleEndian && Avx2.IsSupported && values.Length >= Vector256<double>.Count * 4)
        {
            hasValue = CopyDoubleValuesAndGetStatistics(values,
                MemoryMarshal.Cast<byte, double>(destinationBytes), out min, out max, out nanCount);
        }
        else
        {
            if (BitConverter.IsLittleEndian)
                MemoryMarshal.AsBytes(values).CopyTo(destinationBytes);
            else
                for (var i = 0; i < values.Length; i++)
                    BinaryPrimitives.WriteInt64LittleEndian(destinationBytes[(i * sizeof(double))..],
                        BitConverter.DoubleToInt64Bits(values[i]));
            hasValue = ColumnStatistics.TryGetDoubleMinMax(values, out min, out max, out nanCount);
        }

        writer.Advance(byteCount);
        return ColumnStatistics.FromDoubleAccumulation(min, max, 0, nanCount, hasValue);
    }

    internal static ColumnStatistics WriteAllPresentOptionalDoublePageWithStatistics(
        ReadOnlySpan<double?> values, ref BufferWriter writer)
    {
        var byteCount = checked(values.Length * sizeof(double));
        if (byteCount == 0)
            return ColumnStatistics.FromDoubleAccumulation(0, 0, 0, 0, false);

        var destination = MemoryMarshal.Cast<byte, double>(writer.GetSpan(byteCount)[..byteCount]);
        bool hasValue;
        double min;
        double max;
        long nanCount;
        if (values.Length >= Vector256<double>.Count * 4)
        {
            hasValue = ExtractOptionalDoubleValuesAndGetStatistics(values, destination,
                out min, out max, out nanCount);
        }
        else
        {
            for (var i = 0; i < values.Length; i++)
                destination[i] = values[i]!.Value;
            hasValue = ColumnStatistics.TryGetDoubleMinMax(destination, out min, out max, out nanCount);
        }
        writer.Advance(byteCount);
        return ColumnStatistics.FromDoubleAccumulation(min, max, 0, nanCount, hasValue);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    static bool ExtractOptionalDoubleValuesAndGetStatistics(ReadOnlySpan<double?> values, Span<double> destination,
        out double min, out double max, out long nanCount)
    {
        ref var nullableSource = ref MemoryMarshal.GetReference(values);
        ref var source = ref Unsafe.As<double?, long>(ref nullableSource);
        return CopyDoubleValuesAndGetStatisticsCore<OptionalDoubleVectorSource>(ref source, values.Length,
            destination, out min, out max, out nanCount);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    static bool CopyDoubleValuesAndGetStatistics(ReadOnlySpan<double> values, Span<double> destination,
        out double min, out double max, out long nanCount)
    {
        ref var source = ref Unsafe.As<double, long>(ref MemoryMarshal.GetReference(values));
        return CopyDoubleValuesAndGetStatisticsCore<RequiredDoubleVectorSource>(ref source, values.Length,
            destination, out min, out max, out nanCount);
    }

    interface IDoubleVectorSource
    {
        static abstract Vector256<double> LoadVector(ref long source, nuint valueIndex);
        static abstract double LoadScalar(ref long source, nuint valueIndex);
    }

    readonly struct RequiredDoubleVectorSource : IDoubleVectorSource
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector256<double> LoadVector(ref long source, nuint valueIndex)
            => Vector256.LoadUnsafe(ref source, valueIndex).AsDouble();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double LoadScalar(ref long source, nuint valueIndex)
            => BitConverter.Int64BitsToDouble(Unsafe.Add(ref source, valueIndex));
    }

    readonly struct OptionalDoubleVectorSource : IDoubleVectorSource
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector256<double> LoadVector(ref long source, nuint valueIndex)
        {
            var first = Vector256.LoadUnsafe(ref source, checked(valueIndex * 2));
            var second = Vector256.LoadUnsafe(ref source, checked(valueIndex * 2 + 4));
            var firstValues = Avx2.Permute4x64(first, 0xdd);
            var secondValues = Avx2.Permute4x64(second, 0xdd);
            return Avx2.Permute2x128(firstValues, secondValues, 0x20).AsDouble();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double LoadScalar(ref long source, nuint valueIndex)
            => BitConverter.Int64BitsToDouble(Unsafe.Add(ref source, checked(valueIndex * 2 + 1)));
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    static bool CopyDoubleValuesAndGetStatisticsCore<TSource>(ref long source, int length,
        Span<double> destination, out double min, out double max, out long nanCount)
        where TSource : struct, IDoubleVectorSource
    {
        ref var target = ref MemoryMarshal.GetReference(destination);
        var count = (nuint)length;
        var width = (nuint)Vector256<double>.Count;
        var blockWidth = width * 4;
        var index = (nuint)0;

        var min0 = TSource.LoadVector(ref source, 0);
        var min1 = TSource.LoadVector(ref source, width);
        var min2 = TSource.LoadVector(ref source, width * 2);
        var min3 = TSource.LoadVector(ref source, width * 3);
        var max0 = min0;
        var max1 = min1;
        var max2 = min2;
        var max3 = min3;
        var ordered = Vector256.BitwiseAnd(
            Vector256.BitwiseAnd(Vector256.Equals(min0, min0), Vector256.Equals(min1, min1)),
            Vector256.BitwiseAnd(Vector256.Equals(min2, min2), Vector256.Equals(min3, min3)));
        var signBits = Vector256.BitwiseOr(Vector256.BitwiseOr(min0, min1), Vector256.BitwiseOr(min2, min3));
        min0.StoreUnsafe(ref target);
        min1.StoreUnsafe(ref target, width);
        min2.StoreUnsafe(ref target, width * 2);
        min3.StoreUnsafe(ref target, width * 3);
        index = blockWidth;

        for (; count - index >= blockWidth; index += blockWidth)
        {
            var current0 = TSource.LoadVector(ref source, index);
            var current1 = TSource.LoadVector(ref source, index + width);
            var current2 = TSource.LoadVector(ref source, index + width * 2);
            var current3 = TSource.LoadVector(ref source, index + width * 3);

            current0.StoreUnsafe(ref target, index);
            current1.StoreUnsafe(ref target, index + width);
            current2.StoreUnsafe(ref target, index + width * 2);
            current3.StoreUnsafe(ref target, index + width * 3);

            min0 = Vector256.Min(min0, current0);
            min1 = Vector256.Min(min1, current1);
            min2 = Vector256.Min(min2, current2);
            min3 = Vector256.Min(min3, current3);
            max0 = Vector256.Max(max0, current0);
            max1 = Vector256.Max(max1, current1);
            max2 = Vector256.Max(max2, current2);
            max3 = Vector256.Max(max3, current3);

            ordered = Vector256.BitwiseAnd(ordered, Vector256.BitwiseAnd(
                Vector256.BitwiseAnd(Vector256.Equals(current0, current0), Vector256.Equals(current1, current1)),
                Vector256.BitwiseAnd(Vector256.Equals(current2, current2), Vector256.Equals(current3, current3))));
            signBits = Vector256.BitwiseOr(signBits, Vector256.BitwiseOr(
                Vector256.BitwiseOr(current0, current1), Vector256.BitwiseOr(current2, current3)));
        }

        min0 = Vector256.Min(Vector256.Min(min0, min1), Vector256.Min(min2, min3));
        max0 = Vector256.Max(Vector256.Max(max0, max1), Vector256.Max(max2, max3));
        Span<double> minima = stackalloc double[Vector256<double>.Count];
        Span<double> maxima = stackalloc double[Vector256<double>.Count];
        min0.CopyTo(minima);
        max0.CopyTo(maxima);
        min = minima[0];
        max = maxima[0];
        for (var lane = 1; lane < minima.Length; lane++)
        {
            if (minima[lane] < min)
                min = minima[lane];
            if (maxima[lane] > max)
                max = maxima[lane];
        }

        for (; index < count; index++)
        {
            var value = TSource.LoadScalar(ref source, index);
            Unsafe.Add(ref target, index) = value;
            if (double.IsNaN(value))
            {
                ordered = Vector256<double>.Zero;
                continue;
            }
            if (value < min)
                min = value;
            if (value > max)
                max = value;
            if (BitConverter.DoubleToInt64Bits(value) < 0)
                signBits = Vector256.Create(-0.0d);
        }

        // An all-non-positive page needs the positive-zero half of total zero ordering as well.
        // It is rare and outside the throughput-oriented path, so reuse the scalar implementation.
        if (ordered.ExtractMostSignificantBits() != (1u << Vector256<double>.Count) - 1 || max == 0)
            return ColumnStatistics.TryGetDoubleMinMax(destination, out min, out max, out nanCount);

        nanCount = 0;
        if (min == 0)
            min = signBits.ExtractMostSignificantBits() != 0 ? -0.0d : +0.0d;
        return true;
    }

    static void WriteByteArrayValues<T>(Column column, ReadOnlySpan<T> values, ref BufferWriter writer)
        where T : notnull
    {
        if (typeof(T) == typeof(decimal))
        {
            var decimalValues = Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<decimal>>(ref values);
            var byteCount = 0;
            for (var i = 0; i < decimalValues.Length; i++)
                byteCount = checked(byteCount + sizeof(int) +
                    ParquetDecimalConverter.GetByteCount(decimalValues[i], column));
            if (byteCount == 0)
                return;

            var destination = writer.GetSpan(byteCount);
            var offset = 0;
            for (var i = 0; i < decimalValues.Length; i++)
            {
                var length = ParquetDecimalConverter.GetByteCount(decimalValues[i], column);
                BinaryPrimitives.WriteInt32LittleEndian(destination[offset..], length);
                offset += sizeof(int);
                offset += ParquetDecimalConverter.WriteBigEndian(decimalValues[i], column,
                    destination.Slice(offset, length));
            }
            writer.Advance(offset);
            return;
        }

        if (typeof(T) == typeof(byte[]))
        {
            WriteRequiredByteArrayPayloads(column,
                Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<byte[]>>(ref values), ref writer);
            return;
        }

        if (typeof(T) == typeof(ReadOnlyMemory<byte>))
        {
            WriteLengthPrefixedPayloads<ReadOnlyMemory<byte>, RequiredMemoryRow>(column,
                Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<ReadOnlyMemory<byte>>>(ref values), -1, ref writer);
            return;
        }

        throw new InvalidOperationException(
            $"Column '{column.Name}' expects '{ParquetPhysicalType.ByteArray}' values, but got '{typeof(T)}'.");
    }

    // Required byte[] is a shared-generic reference-type instantiation. Keeping its hot loop
    // concrete lets the JIT eliminate the row-shape dispatch and recover the pre-unification path.
    static void WriteRequiredByteArrayPayloads(Column column, ReadOnlySpan<byte[]> values,
        ref BufferWriter writer)
    {
        var byteCount = 0;
        for (var i = 0; i < values.Length; i++)
        {
            var value = values[i] ?? throw new InvalidOperationException(
                $"Column '{column.Name}' does not support null values.");
            byteCount = checked(byteCount + sizeof(int) + value.Length);
        }

        if (byteCount == 0)
            return;

        var destination = writer.GetSpan(byteCount);
        var offset = 0;
        for (var i = 0; i < values.Length; i++)
        {
            var value = values[i]!;
            BinaryPrimitives.WriteInt32LittleEndian(destination[offset..], value.Length);
            offset += sizeof(int);
            value.CopyTo(destination[offset..]);
            offset += value.Length;
        }

        writer.Advance(offset);
    }

    /// <summary>
    /// Writes as many required byte[] payloads as fit in <paramref name="targetPageBytes"/>, and
    /// returns how many rows that was.
    /// </summary>
    /// <remarks>
    /// The caller reserves the page budget instead of an exact byte count, so a single pass can read
    /// each value's length once, decide whether the value still fits, and copy it. The row that
    /// overflows the budget is left for the next page. A first row larger than the whole budget still
    /// gets written, on a page of its own, which is the rule the split sizing pass used.
    /// <para>
    /// The same pass tracks the column's min and max in <paramref name="minMax"/>, which spares the
    /// statistics pass a walk of its own over the value references. Values are ordered unsigned
    /// lexicographically; see <see cref="PlainBinaryMinMax"/> for who may use the result.
    /// </para>
    /// </remarks>
    internal static int WriteRequiredByteArrayPage(Column column, ReadOnlySpan<byte[]> values, int startIndex,
        int targetPageBytes, ref BufferWriter writer, ref PlainBinaryMinMax minMax,
        out int pageMinIndex, out int pageMaxIndex)
    {
        var first = values[startIndex];
        if (first is null)
            ByteArrayRows.ThrowNullValue(column);
        var destination = writer.GetSpan(Math.Max(targetPageBytes, checked(sizeof(int) + first!.Length)));
        byte[]? pageMin = null;
        byte[]? pageMax = null;
        pageMinIndex = -1;
        pageMaxIndex = -1;
        byte[]? pendingValue = null;
        var pendingIndex = -1;
        var offset = 0;
        var rowCount = 0;
        for (; startIndex + rowCount < values.Length; rowCount++)
        {
            var index = startIndex + rowCount;
            var value = values[index];
            if (value is null)
                ByteArrayRows.ThrowNullValue(column);
            var rowBytes = checked(sizeof(int) + value!.Length);
            // Subtract rather than add: offset can exceed the budget after an oversized first row.
            if (rowCount > 0 && rowBytes > targetPageBytes - offset)
                break;

            BinaryPrimitives.WriteInt32LittleEndian(destination[offset..], value.Length);
            offset += sizeof(int);
            value.CopyTo(destination[offset..]);
            offset += value.Length;

            // A pairwise tournament needs three comparisons for two values instead of comparing
            // both values independently with both extrema. Only the lower value is compared with
            // the page minimum and only the higher value with the maximum.
            if (pendingValue is null)
            {
                pendingValue = value;
                pendingIndex = index;
            }
            else
            {
                byte[] pairMin;
                byte[] pairMax;
                int pairMinIndex;
                int pairMaxIndex;
                if (EncodingPrimitives.ComparePayload(pendingValue, value) <= 0)
                {
                    pairMin = pendingValue;
                    pairMax = value;
                    pairMinIndex = pendingIndex;
                    pairMaxIndex = index;
                }
                else
                {
                    pairMin = value;
                    pairMax = pendingValue;
                    pairMinIndex = index;
                    pairMaxIndex = pendingIndex;
                }

                if (pageMin is null)
                {
                    pageMin = pairMin;
                    pageMax = pairMax;
                    pageMinIndex = pairMinIndex;
                    pageMaxIndex = pairMaxIndex;
                }
                else
                {
                    if (EncodingPrimitives.ComparePayload(pairMin, pageMin) < 0)
                    {
                        pageMin = pairMin;
                        pageMinIndex = pairMinIndex;
                    }
                    if (EncodingPrimitives.ComparePayload(pairMax, pageMax!) > 0)
                    {
                        pageMax = pairMax;
                        pageMaxIndex = pairMaxIndex;
                    }
                }

                pendingValue = null;
            }
        }

        writer.Advance(offset);
        if (pendingValue is not null)
        {
            if (pageMin is null)
            {
                pageMin = pendingValue;
                pageMax = pendingValue;
                pageMinIndex = pendingIndex;
                pageMaxIndex = pendingIndex;
            }
            else
            {
                if (EncodingPrimitives.ComparePayload(pendingValue, pageMin) < 0)
                {
                    pageMin = pendingValue;
                    pageMinIndex = pendingIndex;
                }
                if (EncodingPrimitives.ComparePayload(pendingValue, pageMax!) > 0)
                {
                    pageMax = pendingValue;
                    pageMaxIndex = pendingIndex;
                }
            }
        }

        if (pageMin is not null)
        {
            if (!minMax.Found)
            {
                minMax = new PlainBinaryMinMax
                {
                    Found = true,
                    MinIndex = pageMinIndex,
                    MaxIndex = pageMaxIndex
                };
            }
            else
            {
                if (EncodingPrimitives.ComparePayload(pageMin, values[minMax.MinIndex]) < 0)
                    minMax.MinIndex = pageMinIndex;
                if (EncodingPrimitives.ComparePayload(pageMax!, values[minMax.MaxIndex]) > 0)
                    minMax.MaxIndex = pageMaxIndex;
            }
        }
        return rowCount;
    }

    /// <summary>
    /// The <see cref="ReadOnlyMemory{T}"/> row shape of <see cref="WriteRequiredByteArrayPage"/>. A
    /// required memory row is always present, so this one has no null check to make.
    /// </summary>
    internal static int WriteRequiredMemoryPage(ReadOnlySpan<ReadOnlyMemory<byte>> values, int startIndex,
        int targetPageBytes, ref BufferWriter writer, ref PlainBinaryMinMax minMax)
    {
        var destination = writer.GetSpan(
            Math.Max(targetPageBytes, checked(sizeof(int) + values[startIndex].Length)));
        var found = minMax.Found;
        var minIndex = found ? minMax.MinIndex : 0;
        var maxIndex = found ? minMax.MaxIndex : 0;
        var min = found ? values[minIndex].Span : default;
        var max = found ? values[maxIndex].Span : default;
        var offset = 0;
        var rowCount = 0;
        for (; startIndex + rowCount < values.Length; rowCount++)
        {
            var index = startIndex + rowCount;
            var value = values[index].Span;
            var rowBytes = checked(sizeof(int) + value.Length);
            if (rowCount > 0 && rowBytes > targetPageBytes - offset)
                break;

            BinaryPrimitives.WriteInt32LittleEndian(destination[offset..], value.Length);
            offset += sizeof(int);
            value.CopyTo(destination[offset..]);
            offset += value.Length;

            if (!found)
            {
                found = true;
                min = value;
                max = value;
                minIndex = index;
                maxIndex = index;
            }
            else if (value.SequenceCompareTo(min) < 0)
            {
                min = value;
                minIndex = index;
            }
            else if (value.SequenceCompareTo(max) > 0)
            {
                max = value;
                maxIndex = index;
            }
        }

        writer.Advance(offset);
        if (found)
            minMax = new PlainBinaryMinMax { Found = true, MinIndex = minIndex, MaxIndex = maxIndex };
        return rowCount;
    }

    /// <summary>
    /// Plain BYTE_ARRAY layout: each present value as a little-endian int32 length followed by its
    /// bytes. Absent rows contribute nothing, so this serves the required and optional shapes alike.
    /// </summary>
    static void WriteLengthPrefixedPayloads<TRow, TRowAccess>(Column column, ReadOnlySpan<TRow> rows,
        int knownPayloadBytes, ref BufferWriter writer)
        where TRowAccess : IByteArrayRow<TRow>
    {
        var byteCount = knownPayloadBytes;
        if (byteCount < 0)
        {
            byteCount = 0;
            for (var i = 0; i < rows.Length; i++)
                if (ByteArrayRows.TryGetPayload<TRow, TRowAccess>(column, in rows[i], out var payload))
                    byteCount = checked(byteCount + sizeof(int) + payload.Length);
        }

        if (byteCount == 0)
            return;

        var destination = writer.GetSpan(byteCount);
        var offset = 0;
        for (var i = 0; i < rows.Length; i++)
        {
            if (!ByteArrayRows.TryGetPayload<TRow, TRowAccess>(column, in rows[i], out var payload))
                continue;

            BinaryPrimitives.WriteInt32LittleEndian(destination[offset..], payload.Length);
            offset += sizeof(int);
            payload.CopyTo(destination[offset..]);
            offset += payload.Length;
        }

        writer.Advance(offset);
    }

    /// <summary>
    /// Writes one page of optional plain BYTE_ARRAY payloads. <paramref name="knownPayloadBytes"/> is how
    /// many bytes they occupy once encoded, or -1 when the caller does not know; the page sizer already
    /// measured every row to place the page boundary, so handing that total over saves walking the value
    /// references a second time just to size the destination.
    /// </summary>
    internal static void WriteOptionalValues<TRow, TRowAccess>(Column column, ReadOnlySpan<TRow> rows,
        int knownPayloadBytes, ref BufferWriter writer)
        where TRowAccess : IByteArrayRow<TRow>
    {
        if (typeof(TRow) == typeof(byte[]))
        {
            WriteOptionalByteArrayPayloads(column,
                Unsafe.As<ReadOnlySpan<TRow>, ReadOnlySpan<byte[]>>(ref rows), knownPayloadBytes, ref writer);
            return;
        }

        // INT96 and FIXED_LEN_BYTE_ARRAY columns carry payloads of a known width and are written
        // without a length prefix. Only the byte[] row shape has ever taken this branch: the memory
        // shape falls through to the length-prefixed layout, which is wrong for these physical types
        // but is pre-existing behaviour this refactor deliberately preserves.
        if (typeof(TRow) == typeof(byte[])
            && column.PhysicalType is ParquetPhysicalType.FixedLenByteArray or ParquetPhysicalType.Int96)
        {
            WriteOptionalFixedLengthPayloads<TRow, TRowAccess>(column, rows, ref writer);
            return;
        }

        WriteLengthPrefixedPayloads<TRow, TRowAccess>(column, rows, knownPayloadBytes, ref writer);
    }

    static void WriteOptionalByteArrayPayloads(Column column, ReadOnlySpan<byte[]> values,
        int knownPayloadBytes, ref BufferWriter writer)
    {
        if (column.PhysicalType is ParquetPhysicalType.FixedLenByteArray or ParquetPhysicalType.Int96)
        {
            var valueLength = column.PhysicalType == ParquetPhysicalType.Int96
                ? 12
                : EncodingPrimitives.GetFixedLength(column);
            var fixedByteCount = knownPayloadBytes;
            if (fixedByteCount < 0)
            {
                var presentCount = 0;
                for (var i = 0; i < values.Length; i++)
                    if (values[i] is not null)
                        presentCount++;
                fixedByteCount = checked(presentCount * valueLength);
            }

            var destination = writer.GetSpan(fixedByteCount);
            var offset = 0;
            for (var i = 0; i < values.Length; i++)
            {
                var value = values[i];
                if (value is null)
                    continue;
                if (value.Length != valueLength)
                    throw new InvalidOperationException(
                        $"Column '{column.Name}' expects fixed-length values of {valueLength} bytes, but got {value.Length}.");
                EncodingPrimitives.CopyPayload(value, destination[offset..]);
                offset += valueLength;
            }

            writer.Advance(offset);
            return;
        }

        var byteCount = knownPayloadBytes;
        if (byteCount < 0)
        {
            byteCount = 0;
            for (var i = 0; i < values.Length; i++)
                if (values[i] is { } value)
                    byteCount = checked(byteCount + sizeof(int) + value.Length);
        }

        if (byteCount == 0)
            return;

        var lengthPrefixedDestination = writer.GetSpan(byteCount);
        var lengthPrefixedOffset = 0;
        for (var i = 0; i < values.Length; i++)
        {
            var value = values[i];
            if (value is null)
                continue;
            BinaryPrimitives.WriteInt32LittleEndian(lengthPrefixedDestination[lengthPrefixedOffset..], value.Length);
            lengthPrefixedOffset += sizeof(int);
            EncodingPrimitives.CopyPayload(value, lengthPrefixedDestination[lengthPrefixedOffset..]);
            lengthPrefixedOffset += value.Length;
        }

        writer.Advance(lengthPrefixedOffset);
    }

    /// <summary>
    /// Writes optional plain BYTE_ARRAY values after the page measurement pass established that every
    /// present payload contains exactly one byte. The little-endian caller lets each non-final eight-byte
    /// store overlap the next value's length prefix, then writes the final five-byte value without overlap.
    /// </summary>
    internal static void WriteOptionalSingleByteArrayPayloads(ReadOnlySpan<byte[]> values, int presentCount,
        ref BufferWriter writer)
    {
        var byteCount = checked(presentCount * (sizeof(int) + 1));
        if (byteCount == 0)
            return;

        var destination = writer.GetSpan(byteCount);
        var offset = 0;
        var index = 0;
        var nonFinalPresentCount = presentCount - 1;
        while (nonFinalPresentCount > 0 && index < values.Length)
        {
            var value = values[index++];
            if (value is null)
                continue;
            Unsafe.WriteUnaligned(ref destination[offset], 1UL | (ulong)value[0] << 32);
            offset += sizeof(int) + 1;
            nonFinalPresentCount--;
        }

        while (index < values.Length && values[index] is null)
            index++;
        if (index >= values.Length)
            throw new InvalidOperationException("The measured optional BYTE_ARRAY present count changed while encoding.");
        BinaryPrimitives.WriteInt32LittleEndian(destination[offset..], 1);
        destination[offset + sizeof(int)] = values[index][0];
        offset += sizeof(int) + 1;
        index++;

        while (index < values.Length)
            if (values[index++] is not null)
                throw new InvalidOperationException("The measured optional BYTE_ARRAY present count changed while encoding.");
        if (offset != byteCount)
            throw new InvalidOperationException("The measured optional BYTE_ARRAY present count changed while encoding.");
        writer.Advance(byteCount);
    }

    /// <summary>
    /// The nullable <see cref="ReadOnlyMemory{T}"/> row shape of
    /// <see cref="WriteOptionalSingleByteArrayPayloads"/>. Page measurement has already established that every
    /// present payload contains exactly one byte.
    /// </summary>
    internal static void WriteOptionalSingleByteMemoryPayloads(ReadOnlySpan<ReadOnlyMemory<byte>?> values,
        int presentCount, ref BufferWriter writer)
    {
        var byteCount = checked(presentCount * (sizeof(int) + 1));
        if (byteCount == 0)
            return;

        var destination = writer.GetSpan(byteCount);
        var offset = 0;
        var index = 0;
        var nonFinalPresentCount = presentCount - 1;
        while (nonFinalPresentCount > 0 && index < values.Length)
        {
            var value = values[index++];
            if (!value.HasValue)
                continue;
            Unsafe.WriteUnaligned(ref destination[offset],
                1UL | (ulong)value.GetValueOrDefault().Span[0] << 32);
            offset += sizeof(int) + 1;
            nonFinalPresentCount--;
        }

        while (index < values.Length && !values[index].HasValue)
            index++;
        if (index >= values.Length)
            throw new InvalidOperationException("The measured optional BYTE_ARRAY present count changed while encoding.");
        BinaryPrimitives.WriteInt32LittleEndian(destination[offset..], 1);
        destination[offset + sizeof(int)] = values[index].GetValueOrDefault().Span[0];
        offset += sizeof(int) + 1;
        index++;

        while (index < values.Length)
            if (values[index++].HasValue)
                throw new InvalidOperationException("The measured optional BYTE_ARRAY present count changed while encoding.");
        if (offset != byteCount)
            throw new InvalidOperationException("The measured optional BYTE_ARRAY present count changed while encoding.");
        writer.Advance(byteCount);
    }

    static void WriteOptionalFixedLengthPayloads<TRow, TRowAccess>(Column column, ReadOnlySpan<TRow> rows,
        ref BufferWriter writer)
        where TRowAccess : IByteArrayRow<TRow>
    {
        var valueLength = column.PhysicalType == ParquetPhysicalType.Int96
            ? 12
            : EncodingPrimitives.GetFixedLength(column);
        var presentCount = ByteArrayRows.CountPresent<TRow, TRowAccess>(rows);

        var destination = writer.GetSpan(checked(presentCount * valueLength));
        var offset = 0;
        for (var i = 0; i < rows.Length; i++)
        {
            if (!ByteArrayRows.TryGetPayload<TRow, TRowAccess>(column, in rows[i], out var payload))
                continue;
            if (payload.Length != valueLength)
                throw new InvalidOperationException(
                    $"Column '{column.Name}' expects fixed-length values of {valueLength} bytes, but got {payload.Length}.");
            payload.CopyTo(destination[offset..]);
            offset += valueLength;
        }

        writer.Advance(offset);
    }

    static void WriteInt96Values<T>(Column column, ReadOnlySpan<T> values, ref BufferWriter writer)
        where T : notnull
    {
        if (typeof(T) != typeof(byte[]))
            throw new InvalidOperationException(
                $"Column '{column.Name}' expects '{ParquetPhysicalType.Int96}' values as 12-byte payloads, but got '{typeof(T)}'.");

        var int96Values = Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<byte[]>>(ref values);
        const int int96Size = 12;
        var byteCount = checked(int96Values.Length * int96Size);
        if (byteCount == 0)
            return;

        var destination = writer.GetSpan(byteCount);
        var offset = 0;
        for (var i = 0; i < int96Values.Length; i++)
        {
            var value = int96Values[i] ?? throw new InvalidOperationException(
                $"Column '{column.Name}' does not support null values.");
            if (value.Length != int96Size)
                throw new InvalidOperationException(
                    $"Column '{column.Name}' expects INT96 payloads of {int96Size} bytes, but got {value.Length}.");

            value.CopyTo(destination[offset..]);
            offset += int96Size;
        }

        writer.Advance(offset);
    }

    static void WriteFixedLengthByteArrayValues<T>(Column column, ReadOnlySpan<T> values, ref BufferWriter writer)
        where T : notnull
    {
        var valueLength = EncodingPrimitives.GetFixedLength(column);
        if (typeof(T) == typeof(Guid))
        {
            if (valueLength != 16)
                throw new InvalidOperationException(
                    $"Column '{column.Name}' expects Guid values in fixed-length payloads of 16 bytes, but has length {valueLength}.");

            var guidValues = Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<Guid>>(ref values);
            var guidDestination = writer.GetSpan(checked(guidValues.Length * 16));
            for (var i = 0; i < guidValues.Length; i++)
                guidValues[i].TryWriteBytes(guidDestination.Slice(i * 16, 16), bigEndian: true, out _);
            writer.Advance(checked(guidValues.Length * 16));
            return;
        }

        if (typeof(T) == typeof(decimal))
        {
            var decimalValues = Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<decimal>>(ref values);
            var decimalByteCount = checked(decimalValues.Length * valueLength);
            if (decimalByteCount == 0)
                return;

            var decimalDestination = writer.GetSpan(decimalByteCount);
            for (var i = 0; i < decimalValues.Length; i++)
                ParquetDecimalConverter.WriteFixedBigEndian(decimalValues[i], column,
                    decimalDestination.Slice(i * valueLength, valueLength));
            writer.Advance(decimalByteCount);
            return;
        }

        if (typeof(T) != typeof(byte[]))
            throw new InvalidOperationException(
                $"Column '{column.Name}' expects '{ParquetPhysicalType.FixedLenByteArray}' values as byte[] payloads, but got '{typeof(T)}'.");

        var fixedLengthValues = Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<byte[]>>(ref values);
        var byteCount = checked(fixedLengthValues.Length * valueLength);
        if (byteCount == 0)
            return;

        var destination = writer.GetSpan(byteCount);
        var offset = 0;
        for (var i = 0; i < fixedLengthValues.Length; i++)
        {
            var value = fixedLengthValues[i] ?? throw new InvalidOperationException(
                $"Column '{column.Name}' does not support null values.");
            if (value.Length != valueLength)
                throw new InvalidOperationException(
                    $"Column '{column.Name}' expects fixed-length values of {valueLength} bytes, but got {value.Length}.");

            value.CopyTo(destination[offset..]);
            offset += valueLength;
        }

        writer.Advance(offset);
    }

}
