using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Plank.Schema;

namespace Plank.Writing.Encoding;

static class ByteStreamSplitEncoding
{
    /// <summary>
    /// Values converted onto the stack before being deinterleaved. Matches the threshold the other
    /// converting encoders use so a large page falls back to the heap instead of blowing the stack.
    /// </summary>
    const int MaxStackConvertedValues = 256;

    internal static void WriteValues<T>(Column column, ReadOnlySpan<T> values, ref BufferWriter writer)
        where T : notnull
    {
        switch (column.PhysicalType)
        {
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
            case ParquetPhysicalType.FixedLenByteArray:
                WriteFixedLengthByteArrayValues(column, values, ref writer);
                return;
            default:
                throw new NotSupportedException(
                    $"Encoding '{EncodingKind.ByteStreamSplit}' does not support physical type '{column.PhysicalType}' for column '{column.Name}'.");
        }
    }

    static void WriteInt32Values<T>(Column column, ReadOnlySpan<T> values, ref BufferWriter writer)
        where T : notnull
    {
        var byteCount = checked(values.Length * sizeof(int));
        if (byteCount == 0)
            return;

        var destination = writer.GetSpan(byteCount);
        WriteInt32Lanes(column, values, destination);

        writer.Advance(byteCount);
    }

    static void WriteInt64Values<T>(Column column, ReadOnlySpan<T> values, ref BufferWriter writer)
        where T : notnull
    {
        var byteCount = checked(values.Length * sizeof(long));
        if (byteCount == 0)
            return;

        var destination = writer.GetSpan(byteCount);
        WriteInt64Lanes(column, values, destination);

        writer.Advance(byteCount);
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
        WriteUInt32Lanes(MemoryMarshal.Cast<float, uint>(floatValues), destination);

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
        WriteUInt64Lanes(MemoryMarshal.Cast<double, ulong>(doubleValues), destination);

        writer.Advance(byteCount);
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
            var guidByteCount = checked(guidValues.Length * 16);
            if (guidByteCount == 0)
                return;

            var guidDestination = writer.GetSpan(guidByteCount);
            Span<byte> guidBytes = stackalloc byte[16];
            for (var i = 0; i < guidValues.Length; i++)
            {
                guidValues[i].TryWriteBytes(guidBytes, bigEndian: true, out _);
                ScatterValueAcrossLanes(guidBytes, i, guidValues.Length, guidDestination);
            }

            writer.Advance(guidByteCount);
            return;
        }

        if (typeof(T) == typeof(decimal))
        {
            var decimalValues = Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<decimal>>(ref values);
            var decimalByteCount = checked(decimalValues.Length * valueLength);
            if (decimalByteCount == 0)
                return;

            var decimalDestination = writer.GetSpan(decimalByteCount);
            Span<byte> encoded = valueLength <= 256 ? stackalloc byte[valueLength] : new byte[valueLength];
            for (var i = 0; i < decimalValues.Length; i++)
            {
                ParquetDecimalConverter.WriteFixedBigEndian(decimalValues[i], column, encoded);
                ScatterValueAcrossLanes(encoded, i, decimalValues.Length, decimalDestination);
            }
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

        for (var i = 0; i < fixedLengthValues.Length; i++)
        {
            var value = fixedLengthValues[i] ?? throw new InvalidOperationException(
                $"Column '{column.Name}' does not support null values.");
            if (value.Length != valueLength)
                throw new InvalidOperationException(
                    $"Column '{column.Name}' expects fixed-length values of {valueLength} bytes, but got {value.Length}.");
        }

        var destination = writer.GetSpan(byteCount);
        ref var destinationRef = ref MemoryMarshal.GetReference(destination);
        ref var valuesRef = ref MemoryMarshal.GetReference(fixedLengthValues);
        var valueCount = (nuint)(uint)fixedLengthValues.Length;
        for (nuint lane = 0; lane < (nuint)(uint)valueLength; lane++)
        {
            ref var laneDestination = ref Unsafe.Add(ref destinationRef, lane * valueCount);
            for (nuint i = 0; i < valueCount; i++)
            {
                var value = Unsafe.Add(ref valuesRef, i)!;
                Unsafe.Add(ref laneDestination, i) =
                    Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(value), lane);
            }
        }

        writer.Advance(byteCount);
    }


    static void WriteInt32Lanes<T>(Column column, ReadOnlySpan<T> values, Span<byte> destination)
        where T : notnull
    {
        if (typeof(T) == typeof(int))
        {
            var intValues = Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<int>>(ref values);
            WriteUInt32Lanes(MemoryMarshal.Cast<int, uint>(intValues), destination);
            return;
        }

        if (typeof(T) == typeof(byte))
        {
            // Lane 0 is the value bytes verbatim and lanes 1-3 are the zero high bytes, so the
            // deinterleave degenerates into a copy followed by a clear.
            var byteValues = Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<byte>>(ref values);
            byteValues.CopyTo(destination);
            destination[byteValues.Length..].Clear();
            return;
        }

        if (typeof(T) == typeof(ushort))
        {
            var ushortValues = Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<ushort>>(ref values);
            var lane0 = destination[..ushortValues.Length];
            var lane1 = destination.Slice(ushortValues.Length, ushortValues.Length);
            for (var i = 0; i < ushortValues.Length; i++)
            {
                var value = ushortValues[i];
                lane0[i] = (byte)value;
                lane1[i] = (byte)(value >> 8);
            }

            destination[(ushortValues.Length * 2)..].Clear();
            return;
        }

        if (typeof(T) == typeof(uint))
        {
            var uintValues = Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<uint>>(ref values);
            WriteUInt32Lanes(uintValues, destination);
            return;
        }

        if (typeof(T) == typeof(decimal))
        {
            var decimalValues = Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<decimal>>(ref values);
            Span<uint> converted = decimalValues.Length <= MaxStackConvertedValues
                ? stackalloc uint[decimalValues.Length]
                : new uint[decimalValues.Length];
            for (var i = 0; i < decimalValues.Length; i++)
                converted[i] = unchecked((uint)ParquetDecimalConverter.ToInt32(decimalValues[i], column));
            WriteUInt32Lanes(converted, destination);
            return;
        }

        throw new InvalidOperationException(
            $"Column '{column.Name}' expects '{ParquetPhysicalType.Int32}' values, but got '{typeof(T)}'.");
    }

    static void WriteInt64Lanes<T>(Column column, ReadOnlySpan<T> values, Span<byte> destination)
        where T : notnull
    {
        if (typeof(T) == typeof(long))
        {
            var longValues = Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<long>>(ref values);
            WriteUInt64Lanes(MemoryMarshal.Cast<long, ulong>(longValues), destination);
            return;
        }

        if (typeof(T) == typeof(ulong))
        {
            var ulongValues = Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<ulong>>(ref values);
            WriteUInt64Lanes(ulongValues, destination);
            return;
        }

        if (typeof(T) == typeof(decimal))
        {
            var decimalValues = Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<decimal>>(ref values);
            Span<ulong> converted = decimalValues.Length <= MaxStackConvertedValues
                ? stackalloc ulong[decimalValues.Length]
                : new ulong[decimalValues.Length];
            for (var i = 0; i < decimalValues.Length; i++)
                converted[i] = unchecked((ulong)ParquetDecimalConverter.ToInt64(decimalValues[i], column));
            WriteUInt64Lanes(converted, destination);
            return;
        }

        throw new InvalidOperationException(
            $"Column '{column.Name}' expects '{ParquetPhysicalType.Int64}' values, but got '{typeof(T)}'.");
    }

    static void WriteUInt32Lanes(ReadOnlySpan<uint> values, Span<byte> destination)
    {
        var count = values.Length;
        ref var source = ref MemoryMarshal.GetReference(values);
        ref var lane0 = ref MemoryMarshal.GetReference(destination);
        ref var lane1 = ref Unsafe.Add(ref lane0, count);
        ref var lane2 = ref Unsafe.Add(ref lane1, count);
        ref var lane3 = ref Unsafe.Add(ref lane2, count);
        var length = (nuint)(uint)count;
        nuint i = 0;

        if (BitConverter.IsLittleEndian && Avx512F.IsSupported)
        {
            var vectorCount = (nuint)Vector512<uint>.Count;
            if (length >= vectorCount)
            {
                var lastVector = length - vectorCount;
                for (; i <= lastVector; i += vectorCount)
                {
                    var vector = Vector512.LoadUnsafe(ref source, i);
                    NarrowToByte(vector).StoreUnsafe(ref lane0, i);
                    NarrowToByte(Vector512.ShiftRightLogical(vector, 8)).StoreUnsafe(ref lane1, i);
                    NarrowToByte(Vector512.ShiftRightLogical(vector, 16)).StoreUnsafe(ref lane2, i);
                    NarrowToByte(Vector512.ShiftRightLogical(vector, 24)).StoreUnsafe(ref lane3, i);
                }
            }
        }

        for (; i < length; i++)
        {
            var value = Unsafe.Add(ref source, i);
            Unsafe.Add(ref lane0, i) = (byte)value;
            Unsafe.Add(ref lane1, i) = (byte)(value >> 8);
            Unsafe.Add(ref lane2, i) = (byte)(value >> 16);
            Unsafe.Add(ref lane3, i) = (byte)(value >> 24);
        }
    }

    static void WriteUInt64Lanes(ReadOnlySpan<ulong> values, Span<byte> destination)
    {
        var count = values.Length;
        ref var source = ref MemoryMarshal.GetReference(values);
        ref var lane0 = ref MemoryMarshal.GetReference(destination);
        ref var lane1 = ref Unsafe.Add(ref lane0, count);
        ref var lane2 = ref Unsafe.Add(ref lane1, count);
        ref var lane3 = ref Unsafe.Add(ref lane2, count);
        ref var lane4 = ref Unsafe.Add(ref lane3, count);
        ref var lane5 = ref Unsafe.Add(ref lane4, count);
        ref var lane6 = ref Unsafe.Add(ref lane5, count);
        ref var lane7 = ref Unsafe.Add(ref lane6, count);
        var length = (nuint)(uint)count;
        nuint i = 0;

        if (BitConverter.IsLittleEndian && Avx512F.IsSupported)
        {
            var vectorCount = (nuint)Vector512<ulong>.Count;
            if (length >= vectorCount)
            {
                var lastVector = length - vectorCount;
                for (; i <= lastVector; i += vectorCount)
                {
                    var vector = Vector512.LoadUnsafe(ref source, i);
                    StoreLowerUInt64(NarrowToByte(vector), ref lane0, i);
                    StoreLowerUInt64(NarrowToByte(Vector512.ShiftRightLogical(vector, 8)), ref lane1, i);
                    StoreLowerUInt64(NarrowToByte(Vector512.ShiftRightLogical(vector, 16)), ref lane2, i);
                    StoreLowerUInt64(NarrowToByte(Vector512.ShiftRightLogical(vector, 24)), ref lane3, i);
                    StoreLowerUInt64(NarrowToByte(Vector512.ShiftRightLogical(vector, 32)), ref lane4, i);
                    StoreLowerUInt64(NarrowToByte(Vector512.ShiftRightLogical(vector, 40)), ref lane5, i);
                    StoreLowerUInt64(NarrowToByte(Vector512.ShiftRightLogical(vector, 48)), ref lane6, i);
                    StoreLowerUInt64(NarrowToByte(Vector512.ShiftRightLogical(vector, 56)), ref lane7, i);
                }
            }
        }

        for (; i < length; i++)
        {
            var value = Unsafe.Add(ref source, i);
            Unsafe.Add(ref lane0, i) = (byte)value;
            Unsafe.Add(ref lane1, i) = (byte)(value >> 8);
            Unsafe.Add(ref lane2, i) = (byte)(value >> 16);
            Unsafe.Add(ref lane3, i) = (byte)(value >> 24);
            Unsafe.Add(ref lane4, i) = (byte)(value >> 32);
            Unsafe.Add(ref lane5, i) = (byte)(value >> 40);
            Unsafe.Add(ref lane6, i) = (byte)(value >> 48);
            Unsafe.Add(ref lane7, i) = (byte)(value >> 56);
        }
    }

    /// <summary>
    /// Writes one already-encoded fixed-length payload into the byte-stream-split lanes: byte
    /// <c>lane</c> of value <paramref name="valueIndex"/> lands at <c>lane * valueCount + valueIndex</c>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void ScatterValueAcrossLanes(ReadOnlySpan<byte> encoded, int valueIndex, int valueCount,
        Span<byte> destination)
    {
        for (var lane = 0; lane < encoded.Length; lane++)
            destination[(lane * valueCount) + valueIndex] = encoded[lane];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static Vector128<byte> NarrowToByte(Vector512<uint> values)
        => Avx512F.ConvertToVector128Byte(values);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static Vector128<byte> NarrowToByte(Vector512<ulong> values)
        => Avx512F.ConvertToVector128Byte(values);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static unsafe void StoreLowerUInt64(Vector128<byte> values, ref byte destination, nuint offset)
        => Sse2.StoreScalar((ulong*)Unsafe.AsPointer(ref Unsafe.Add(ref destination, offset)), values.AsUInt64());

}
