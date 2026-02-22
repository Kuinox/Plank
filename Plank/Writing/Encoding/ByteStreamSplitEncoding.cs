using System.Runtime.CompilerServices;
using Plank.Schema;

namespace Plank.Writing;

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
        for (var lane = 0; lane < sizeof(int); lane++)
            for (var i = 0; i < intValues.Length; i++)
                destination[lane * intValues.Length + i] = (byte)(intValues[i] >> (lane * 8));

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
        for (var lane = 0; lane < sizeof(long); lane++)
            for (var i = 0; i < longValues.Length; i++)
                destination[lane * longValues.Length + i] = (byte)(longValues[i] >> (lane * 8));

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
        for (var lane = 0; lane < sizeof(float); lane++)
            for (var i = 0; i < floatValues.Length; i++)
                destination[lane * floatValues.Length + i] =
                    (byte)(BitConverter.SingleToInt32Bits(floatValues[i]) >> (lane * 8));

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
        for (var lane = 0; lane < sizeof(double); lane++)
            for (var i = 0; i < doubleValues.Length; i++)
                destination[lane * doubleValues.Length + i] =
                    (byte)(BitConverter.DoubleToInt64Bits(doubleValues[i]) >> (lane * 8));

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

        var destination = writer.GetSpan(byteCount);
        for (var lane = 0; lane < valueLength; lane++)
        {
            var baseOffset = lane * fixedLengthValues.Length;
            for (var i = 0; i < fixedLengthValues.Length; i++)
            {
                var value = fixedLengthValues[i] ?? throw new InvalidOperationException(
                    $"Column '{column.Name}' does not support null values.");
                if (value.Length != valueLength)
                    throw new InvalidOperationException(
                        $"Column '{column.Name}' expects fixed-length values of {valueLength} bytes, but got {value.Length}.");

                destination[baseOffset + i] = value[lane];
            }
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
