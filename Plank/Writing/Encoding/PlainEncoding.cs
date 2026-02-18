using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Plank.Schema;

namespace Plank.Writing;

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
                throw new NotSupportedException("Parquet INT96 encoding is TODO.");
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

        var destination = writer.GetSpan(byteCount);
        destination[..byteCount].Clear();
        for (var i = 0; i < booleanValues.Length; i++)
            if (booleanValues[i])
                destination[i >> 3] |= (byte)(1 << (i & 7));

        writer.Advance(byteCount);
    }

    static void WriteInt32Values<T>(Column column, ReadOnlySpan<T> values, ref BufferWriter writer)
        where T : notnull
    {
        if (typeof(T) != typeof(int))
            throw new InvalidOperationException(
                $"Column '{column.Name}' expects '{ParquetPhysicalType.Int32}' values, but got '{typeof(T)}'.");

        var intValues = Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<int>>(ref values);
        var byteCount = checked(intValues.Length * sizeof(int));
        if (byteCount == 0)
            return;

        var destination = writer.GetSpan(byteCount);
        if (BitConverter.IsLittleEndian)
            MemoryMarshal.AsBytes(intValues).CopyTo(destination);
        else
            for (var i = 0; i < intValues.Length; i++)
                BinaryPrimitives.WriteInt32LittleEndian(destination[(i * sizeof(int))..], intValues[i]);

        writer.Advance(byteCount);
    }

    static void WriteInt64Values<T>(Column column, ReadOnlySpan<T> values, ref BufferWriter writer)
        where T : notnull
    {
        if (typeof(T) != typeof(long))
            throw new InvalidOperationException(
                $"Column '{column.Name}' expects '{ParquetPhysicalType.Int64}' values, but got '{typeof(T)}'.");

        var longValues = Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<long>>(ref values);
        var byteCount = checked(longValues.Length * sizeof(long));
        if (byteCount == 0)
            return;

        var destination = writer.GetSpan(byteCount);
        if (BitConverter.IsLittleEndian)
            MemoryMarshal.AsBytes(longValues).CopyTo(destination);
        else
            for (var i = 0; i < longValues.Length; i++)
                BinaryPrimitives.WriteInt64LittleEndian(destination[(i * sizeof(long))..], longValues[i]);

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
        if (typeof(T) != typeof(byte[]))
            throw new InvalidOperationException(
                $"Column '{column.Name}' expects '{ParquetPhysicalType.ByteArray}' values, but got '{typeof(T)}'.");

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
    }

    static void WriteFixedLengthByteArrayValues<T>(Column column, ReadOnlySpan<T> values, ref BufferWriter writer)
        where T : notnull
    {
        if (typeof(T) != typeof(byte[]))
            throw new InvalidOperationException(
                $"Column '{column.Name}' expects '{ParquetPhysicalType.FixedLenByteArray}' values, but got '{typeof(T)}'.");

        var byteArrayValues = Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<byte[]>>(ref values);
        if (byteArrayValues.Length == 0)
            return;

        var fixedLength = byteArrayValues[0]?.Length ?? throw new InvalidOperationException(
            $"Column '{column.Name}' does not support null values.");
        var byteCount = checked(byteArrayValues.Length * fixedLength);
        var destination = writer.GetSpan(byteCount);
        var offset = 0;
        for (var i = 0; i < byteArrayValues.Length; i++)
        {
            var value = byteArrayValues[i] ?? throw new InvalidOperationException(
                $"Column '{column.Name}' does not support null values.");
            if (value.Length != fixedLength)
                throw new InvalidOperationException(
                    $"Column '{column.Name}' expects fixed-length byte arrays of length {fixedLength}, but got {value.Length}.");

            value.CopyTo(destination[offset..]);
            offset += fixedLength;
        }

        writer.Advance(offset);
    }
}
