using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Plank.Schema;

namespace Plank.Writing.Encoding;

static class ByteStreamSplitEncoding
{
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
        if (typeof(T) != typeof(int))
            throw new InvalidOperationException(
                $"Column '{column.Name}' expects '{ParquetPhysicalType.Int32}' values, but got '{typeof(T)}'.");

        var intValues = Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<int>>(ref values);
        var byteCount = checked(intValues.Length * sizeof(int));
        if (byteCount == 0)
            return;

        var destination = writer.GetSpan(byteCount);
        var lane0 = destination[..intValues.Length];
        var lane1 = destination.Slice(intValues.Length, intValues.Length);
        var lane2 = destination.Slice(intValues.Length * 2, intValues.Length);
        var lane3 = destination.Slice(intValues.Length * 3, intValues.Length);
        for (var i = 0; i < intValues.Length; i++)
        {
            var value = intValues[i];
            lane0[i] = (byte)value;
            lane1[i] = (byte)(value >> 8);
            lane2[i] = (byte)(value >> 16);
            lane3[i] = (byte)(value >> 24);
        }

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
        var lane0 = destination[..longValues.Length];
        var lane1 = destination.Slice(longValues.Length, longValues.Length);
        var lane2 = destination.Slice(longValues.Length * 2, longValues.Length);
        var lane3 = destination.Slice(longValues.Length * 3, longValues.Length);
        var lane4 = destination.Slice(longValues.Length * 4, longValues.Length);
        var lane5 = destination.Slice(longValues.Length * 5, longValues.Length);
        var lane6 = destination.Slice(longValues.Length * 6, longValues.Length);
        var lane7 = destination.Slice(longValues.Length * 7, longValues.Length);
        for (var i = 0; i < longValues.Length; i++)
        {
            var value = longValues[i];
            lane0[i] = (byte)value;
            lane1[i] = (byte)(value >> 8);
            lane2[i] = (byte)(value >> 16);
            lane3[i] = (byte)(value >> 24);
            lane4[i] = (byte)(value >> 32);
            lane5[i] = (byte)(value >> 40);
            lane6[i] = (byte)(value >> 48);
            lane7[i] = (byte)(value >> 56);
        }

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
        var intValues = MemoryMarshal.Cast<float, int>(floatValues);
        var lane0 = destination[..intValues.Length];
        var lane1 = destination.Slice(intValues.Length, intValues.Length);
        var lane2 = destination.Slice(intValues.Length * 2, intValues.Length);
        var lane3 = destination.Slice(intValues.Length * 3, intValues.Length);

        for (var i = 0; i < intValues.Length; i++)
            lane0[i] = (byte)intValues[i];
        for (var i = 0; i < intValues.Length; i++)
            lane1[i] = (byte)(intValues[i] >> 8);
        for (var i = 0; i < intValues.Length; i++)
            lane2[i] = (byte)(intValues[i] >> 16);
        for (var i = 0; i < intValues.Length; i++)
            lane3[i] = (byte)(intValues[i] >> 24);

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
        var longValues = MemoryMarshal.Cast<double, long>(doubleValues);
        var lane0 = destination[..longValues.Length];
        var lane1 = destination.Slice(longValues.Length, longValues.Length);
        var lane2 = destination.Slice(longValues.Length * 2, longValues.Length);
        var lane3 = destination.Slice(longValues.Length * 3, longValues.Length);
        var lane4 = destination.Slice(longValues.Length * 4, longValues.Length);
        var lane5 = destination.Slice(longValues.Length * 5, longValues.Length);
        var lane6 = destination.Slice(longValues.Length * 6, longValues.Length);
        var lane7 = destination.Slice(longValues.Length * 7, longValues.Length);
        for (var i = 0; i < longValues.Length; i++)
            lane0[i] = (byte)longValues[i];
        for (var i = 0; i < longValues.Length; i++)
            lane1[i] = (byte)(longValues[i] >> 8);
        for (var i = 0; i < longValues.Length; i++)
            lane2[i] = (byte)(longValues[i] >> 16);
        for (var i = 0; i < longValues.Length; i++)
            lane3[i] = (byte)(longValues[i] >> 24);
        for (var i = 0; i < longValues.Length; i++)
            lane4[i] = (byte)(longValues[i] >> 32);
        for (var i = 0; i < longValues.Length; i++)
            lane5[i] = (byte)(longValues[i] >> 40);
        for (var i = 0; i < longValues.Length; i++)
            lane6[i] = (byte)(longValues[i] >> 48);
        for (var i = 0; i < longValues.Length; i++)
            lane7[i] = (byte)(longValues[i] >> 56);

        writer.Advance(byteCount);
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

        for (var i = 0; i < fixedLengthValues.Length; i++)
        {
            var value = fixedLengthValues[i] ?? throw new InvalidOperationException(
                $"Column '{column.Name}' does not support null values.");
            if (value.Length != valueLength)
                throw new InvalidOperationException(
                    $"Column '{column.Name}' expects fixed-length values of {valueLength} bytes, but got {value.Length}.");
        }

        var destination = writer.GetSpan(byteCount);
        for (var lane = 0; lane < valueLength; lane++)
        {
            var baseOffset = lane * fixedLengthValues.Length;
            for (var i = 0; i < fixedLengthValues.Length; i++)
                destination[baseOffset + i] = fixedLengthValues[i]![lane];
        }

        writer.Advance(byteCount);
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
