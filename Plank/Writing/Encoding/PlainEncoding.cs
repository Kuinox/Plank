using System.Buffers.Binary;
using System.Text;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Plank.Schema;
using TextEncoding = System.Text.Encoding;

namespace Plank.Writing.Encoding;

static class PlainEncoding
{
    static readonly TextEncoding Utf8 = TextEncoding.UTF8;

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

        var sourceBytes = MemoryMarshal.AsBytes(booleanValues);
        var destination = writer.GetSpan(byteCount);
        var fullByteCount = booleanValues.Length >> 3;
        var valueIndex = 0;
        var byteIndex = 0;
        if (Sse2.IsSupported)
        {
            var simdValueCount = sourceBytes.Length & ~15;
            for (; valueIndex < simdValueCount; valueIndex += 16)
            {
                var chunk = MemoryMarshal.Read<Vector128<byte>>(sourceBytes[valueIndex..]);
                var gtZero = Sse2.CompareGreaterThan(chunk.AsSByte(), Vector128<sbyte>.Zero);
                var mask = Sse2.MoveMask(gtZero);
                destination[byteIndex] = (byte)mask;
                destination[byteIndex + 1] = (byte)(mask >> 8);
                byteIndex += 2;
            }
        }

        for (; byteIndex < fullByteCount; byteIndex++)
        {
            var packed =
                (booleanValues[valueIndex] ? 1 : 0) |
                ((booleanValues[valueIndex + 1] ? 1 : 0) << 1) |
                ((booleanValues[valueIndex + 2] ? 1 : 0) << 2) |
                ((booleanValues[valueIndex + 3] ? 1 : 0) << 3) |
                ((booleanValues[valueIndex + 4] ? 1 : 0) << 4) |
                ((booleanValues[valueIndex + 5] ? 1 : 0) << 5) |
                ((booleanValues[valueIndex + 6] ? 1 : 0) << 6) |
                ((booleanValues[valueIndex + 7] ? 1 : 0) << 7);
            destination[byteIndex] = (byte)packed;
            valueIndex += 8;
        }

        var tailCount = booleanValues.Length - valueIndex;
        if (tailCount > 0)
        {
            var packed = 0;
            for (var bit = 0; bit < tailCount; bit++)
                packed |= (booleanValues[valueIndex + bit] ? 1 : 0) << bit;
            destination[byteIndex] = (byte)packed;
        }

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
            for (var i = 0; i < byteValues.Length; i++)
                BinaryPrimitives.WriteInt32LittleEndian(destination[(i * sizeof(int))..], byteValues[i]);
            writer.Advance(byteCount);
            return;
        }

        if (typeof(T) == typeof(ushort))
        {
            var ushortValues = Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<ushort>>(ref values);
            for (var i = 0; i < ushortValues.Length; i++)
                BinaryPrimitives.WriteInt32LittleEndian(destination[(i * sizeof(int))..], ushortValues[i]);
            writer.Advance(byteCount);
            return;
        }

        if (typeof(T) == typeof(uint))
        {
            var uintValues = Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<uint>>(ref values);
            for (var i = 0; i < uintValues.Length; i++)
                BinaryPrimitives.WriteUInt32LittleEndian(destination[(i * sizeof(int))..], uintValues[i]);
            writer.Advance(byteCount);
            return;
        }

        throw new InvalidOperationException(
            $"Column '{column.Name}' expects '{ParquetPhysicalType.Int32}' values, but got '{typeof(T)}'.");
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
            for (var i = 0; i < ulongValues.Length; i++)
                BinaryPrimitives.WriteUInt64LittleEndian(destination[(i * sizeof(long))..], ulongValues[i]);
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
        if (typeof(T) == typeof(byte[]))
        {
            var byteArrayValues = Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<byte[]>>(ref values);
            var byteCount = 0;
            for (var i = 0; i < byteArrayValues.Length; i++)
            {
                var value = byteArrayValues[i] ?? throw new InvalidOperationException(
                    $"Column '{column.Name}' does not support null values.");

                byteCount = checked(byteCount + sizeof(int) + value.Length);
            }

            if (byteCount == 0)
                return;

            var destination = writer.GetSpan(byteCount);
            var offset = 0;
            for (var i = 0; i < byteArrayValues.Length; i++)
            {
                var value = byteArrayValues[i]!;
                BinaryPrimitives.WriteInt32LittleEndian(destination[offset..], value.Length);
                offset += sizeof(int);
                value.CopyTo(destination[offset..]);
                offset += value.Length;
            }

            writer.Advance(offset);
            return;
        }

        if (typeof(T) == typeof(ReadOnlyMemory<byte>))
        {
            var memoryValues = Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<ReadOnlyMemory<byte>>>(ref values);
            var byteCount = 0;
            for (var i = 0; i < memoryValues.Length; i++)
                byteCount = checked(byteCount + sizeof(int) + memoryValues[i].Length);
            if (byteCount == 0)
                return;

            var destination = writer.GetSpan(byteCount);
            var offset = 0;
            for (var i = 0; i < memoryValues.Length; i++)
            {
                var value = memoryValues[i];
                BinaryPrimitives.WriteInt32LittleEndian(destination[offset..], value.Length);
                offset += sizeof(int);
                value.Span.CopyTo(destination[offset..]);
                offset += value.Length;
            }

            writer.Advance(offset);
            return;
        }

        if (typeof(T) == typeof(string))
        {
            var stringValues = Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<string>>(ref values);
            var byteCount = 0;
            for (var i = 0; i < stringValues.Length; i++)
            {
                var value = stringValues[i] ?? throw new InvalidOperationException(
                    $"Column '{column.Name}' does not support null values.");
                byteCount = checked(byteCount + sizeof(int) + Utf8.GetByteCount(value));
            }

            if (byteCount == 0)
                return;

            var destination = writer.GetSpan(byteCount);
            var offset = 0;
            for (var i = 0; i < stringValues.Length; i++)
            {
                var value = stringValues[i]!;
                var utf8Length = Utf8.GetByteCount(value);
                BinaryPrimitives.WriteInt32LittleEndian(destination[offset..], utf8Length);
                offset += sizeof(int);
                var written = Utf8.GetBytes(value, destination[offset..]);
                offset += written;
            }

            writer.Advance(offset);
            return;
        }

        throw new InvalidOperationException(
            $"Column '{column.Name}' expects '{ParquetPhysicalType.ByteArray}' values, but got '{typeof(T)}'.");
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
        if (typeof(T) != typeof(byte[]))
            throw new InvalidOperationException(
                $"Column '{column.Name}' expects '{ParquetPhysicalType.FixedLenByteArray}' values as byte[] payloads, but got '{typeof(T)}'.");

        var valueLength = GetFixedLength(column);
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

    static int GetFixedLength(Column column)
    {
        var valueLength = column.Options.TypeLength;
        if (valueLength == 0)
            throw new InvalidOperationException(
                $"Column '{column.Name}' is '{ParquetPhysicalType.FixedLenByteArray}' and requires a positive '{nameof(ColumnOptions.TypeLength)}'.");
        if (valueLength > int.MaxValue)
            throw new InvalidOperationException(
                $"Column '{column.Name}' fixed length ({valueLength}) exceeds supported maximum of {int.MaxValue}.");

        return checked((int)valueLength);
    }
}
