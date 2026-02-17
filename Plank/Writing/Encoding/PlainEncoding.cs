using System.Buffers.Binary;
using Plank.Schema;

namespace Plank.Writing;

static class PlainEncoding
{
    internal static void WriteValue<T>(Column column, T value, ref BufferWriter writer)
        where T : notnull
    {
        switch (column.PhysicalType)
        {
            case ParquetPhysicalType.Boolean:
            {
                if (value is not bool booleanValue)
                    throw new InvalidOperationException(
                        $"Column '{column.Name}' expects '{ParquetPhysicalType.Boolean}' values, but got '{typeof(T)}'.");

                var destination = writer.GetSpan(1);
                destination[0] = booleanValue ? (byte)1 : (byte)0;
                writer.Advance(1);
                return;
            }
            case ParquetPhysicalType.Int32:
            {
                if (value is not int int32Value)
                    throw new InvalidOperationException(
                        $"Column '{column.Name}' expects '{ParquetPhysicalType.Int32}' values, but got '{typeof(T)}'.");

                var destination = writer.GetSpan(sizeof(int));
                BinaryPrimitives.WriteInt32LittleEndian(destination, int32Value);
                writer.Advance(sizeof(int));
                return;
            }
            case ParquetPhysicalType.Int64:
            {
                if (value is not long int64Value)
                    throw new InvalidOperationException(
                        $"Column '{column.Name}' expects '{ParquetPhysicalType.Int64}' values, but got '{typeof(T)}'.");

                var destination = writer.GetSpan(sizeof(long));
                BinaryPrimitives.WriteInt64LittleEndian(destination, int64Value);
                writer.Advance(sizeof(long));
                return;
            }
            case ParquetPhysicalType.Float:
            {
                if (value is not float floatValue)
                    throw new InvalidOperationException(
                        $"Column '{column.Name}' expects '{ParquetPhysicalType.Float}' values, but got '{typeof(T)}'.");

                var destination = writer.GetSpan(sizeof(float));
                BinaryPrimitives.WriteInt32LittleEndian(destination, BitConverter.SingleToInt32Bits(floatValue));
                writer.Advance(sizeof(float));
                return;
            }
            case ParquetPhysicalType.Double:
            {
                if (value is not double doubleValue)
                    throw new InvalidOperationException(
                        $"Column '{column.Name}' expects '{ParquetPhysicalType.Double}' values, but got '{typeof(T)}'.");

                var destination = writer.GetSpan(sizeof(double));
                BinaryPrimitives.WriteInt64LittleEndian(destination, BitConverter.DoubleToInt64Bits(doubleValue));
                writer.Advance(sizeof(double));
                return;
            }
            case ParquetPhysicalType.ByteArray:
            {
                if (value is not byte[] byteArrayValue)
                    throw new InvalidOperationException(
                        $"Column '{column.Name}' expects '{ParquetPhysicalType.ByteArray}' values, but got '{typeof(T)}'.");

                var size = checked(sizeof(int) + byteArrayValue.Length);
                var destination = writer.GetSpan(size);
                BinaryPrimitives.WriteInt32LittleEndian(destination, byteArrayValue.Length);
                byteArrayValue.CopyTo(destination[sizeof(int)..]);
                writer.Advance(size);
                return;
            }
            case ParquetPhysicalType.FixedLenByteArray:
            {
                if (value is not byte[] byteArrayValue)
                    throw new InvalidOperationException(
                        $"Column '{column.Name}' expects '{ParquetPhysicalType.FixedLenByteArray}' values, but got '{typeof(T)}'.");

                var destination = writer.GetSpan(byteArrayValue.Length);
                byteArrayValue.CopyTo(destination);
                writer.Advance(byteArrayValue.Length);
                return;
            }
            case ParquetPhysicalType.Int96:
                throw new NotSupportedException("Parquet INT96 encoding is TODO.");
            default:
                throw new NotSupportedException(
                    $"Physical type '{column.PhysicalType}' is not supported by plain encoding.");
        }
    }
}
