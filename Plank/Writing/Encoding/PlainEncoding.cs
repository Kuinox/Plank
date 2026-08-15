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
            WriteLengthPrefixedPayloads<byte[], RequiredByteArrayRow>(column,
                Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<byte[]>>(ref values), ref writer);
            return;
        }

        if (typeof(T) == typeof(ReadOnlyMemory<byte>))
        {
            WriteLengthPrefixedPayloads<ReadOnlyMemory<byte>, RequiredMemoryRow>(column,
                Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<ReadOnlyMemory<byte>>>(ref values), ref writer);
            return;
        }

        throw new InvalidOperationException(
            $"Column '{column.Name}' expects '{ParquetPhysicalType.ByteArray}' values, but got '{typeof(T)}'.");
    }

    /// <summary>
    /// Plain BYTE_ARRAY layout: each present value as a little-endian int32 length followed by its
    /// bytes. Absent rows contribute nothing, so this serves the required and optional shapes alike.
    /// </summary>
    static void WriteLengthPrefixedPayloads<TRow, TRowAccess>(Column column, ReadOnlySpan<TRow> rows,
        ref BufferWriter writer)
        where TRowAccess : IByteArrayRow<TRow>
    {
        var byteCount = 0;
        for (var i = 0; i < rows.Length; i++)
            if (ByteArrayRows.TryGetPayload<TRow, TRowAccess>(column, in rows[i], out var payload))
                byteCount = checked(byteCount + sizeof(int) + payload.Length);

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

    internal static void WriteOptionalValues<TRow, TRowAccess>(Column column, ReadOnlySpan<TRow> rows,
        ref BufferWriter writer)
        where TRowAccess : IByteArrayRow<TRow>
    {
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

        WriteLengthPrefixedPayloads<TRow, TRowAccess>(column, rows, ref writer);
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
