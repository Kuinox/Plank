using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Plank.Writing;

static partial class Encoding
{
    internal static class Plain
    {
        internal static void EncodeInt32(ReadOnlySpan<int> values, ref DestinationBufferWriter writer,
            ref ParquetWriter.RowGroupState.ColumnState state, string columnName, int maxEncodedBytes)
        {
            var byteCount = checked(values.Length * sizeof(int));
            if (byteCount == 0)
                throw new InvalidOperationException(
                    $"Column '{columnName}' requires {byteCount} bytes but encoded buffer capacity is {maxEncodedBytes}.");
            var destination = writer.GetSpan(byteCount);

            if (BitConverter.IsLittleEndian)
                MemoryMarshal.AsBytes(values).CopyTo(destination);
            else
                for (var i = 0; i < values.Length; i++)
                    BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(i * 4, 4), values[i]);

            writer.Advance(byteCount);
            state.EncodedLength = byteCount;
            state.UncompressedLength = byteCount;
        }

        internal static void EncodeBoolean(ReadOnlySpan<bool> values, ref DestinationBufferWriter writer,
            ref ParquetWriter.RowGroupState.ColumnState state, string columnName, int maxEncodedBytes)
        {
            var byteCount = (values.Length + 7) >> 3;
            if (byteCount == 0)
                throw new InvalidOperationException(
                    $"Column '{columnName}' requires {byteCount} bytes but encoded buffer capacity is {maxEncodedBytes}.");
            var destination = writer.GetSpan(byteCount);

            destination.Clear();
            for (var i = 0; i < values.Length; i++)
            {
                if (!values[i])
                    continue;

                var byteIndex = i >> 3;
                var bitIndex = i & 7;
                destination[byteIndex] |= (byte)(1 << bitIndex);
            }

            writer.Advance(byteCount);
            state.EncodedLength = byteCount;
            state.UncompressedLength = byteCount;
        }

        internal static void EncodeDateOnly(ReadOnlySpan<DateOnly> values, ref DestinationBufferWriter writer,
            ref ParquetWriter.RowGroupState.ColumnState state, string columnName, int maxEncodedBytes)
        {
            var byteCount = checked(values.Length * sizeof(int));
            if (byteCount == 0)
                throw new InvalidOperationException(
                    $"Column '{columnName}' requires {byteCount} bytes but encoded buffer capacity is {maxEncodedBytes}.");
            var destination = writer.GetSpan(byteCount);

            for (var i = 0; i < values.Length; i++)
            {
                var daysSinceEpoch = checked(values[i].DayNumber - Encoding.UnixEpochDayNumber);
                BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(i * 4, 4), daysSinceEpoch);
            }

            writer.Advance(byteCount);
            state.EncodedLength = byteCount;
            state.UncompressedLength = byteCount;
        }

        internal static void EncodeInt64(ReadOnlySpan<long> values, ref DestinationBufferWriter writer,
            ref ParquetWriter.RowGroupState.ColumnState state, string columnName, int maxEncodedBytes)
        {
            var byteCount = checked(values.Length * sizeof(long));
            if (byteCount == 0)
                throw new InvalidOperationException(
                    $"Column '{columnName}' requires {byteCount} bytes but encoded buffer capacity is {maxEncodedBytes}.");
            var destination = writer.GetSpan(byteCount);

            if (BitConverter.IsLittleEndian)
                MemoryMarshal.AsBytes(values).CopyTo(destination);
            else
                for (var i = 0; i < values.Length; i++)
                    BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(i * 8, 8), values[i]);

            writer.Advance(byteCount);
            state.EncodedLength = byteCount;
            state.UncompressedLength = byteCount;
        }

        internal static void EncodeDateTime(ReadOnlySpan<DateTime> values, DateTimeKindHandling dateTimeKindHandling,
            ref DestinationBufferWriter writer, ref ParquetWriter.RowGroupState.ColumnState state,
            string columnName, int maxEncodedBytes)
        {
            var byteCount = checked(values.Length * sizeof(long));
            if (byteCount == 0)
                throw new InvalidOperationException(
                    $"Column '{columnName}' requires {byteCount} bytes but encoded buffer capacity is {maxEncodedBytes}.");
            var destination = writer.GetSpan(byteCount);

            Encoding.ValidateDateTimeHandling(dateTimeKindHandling, columnName);
            for (var i = 0; i < values.Length; i++)
            {
                var unixMicros = Encoding.ToUnixMicroseconds(values[i], dateTimeKindHandling, columnName);
                BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(i * 8, 8), unixMicros);
            }

            writer.Advance(byteCount);
            state.EncodedLength = byteCount;
            state.UncompressedLength = byteCount;
        }

        internal static void EncodeDateTimeOffset(ReadOnlySpan<DateTimeOffset> values,
            ref DestinationBufferWriter writer, ref ParquetWriter.RowGroupState.ColumnState state,
            string columnName, int maxEncodedBytes)
        {
            var byteCount = checked(values.Length * sizeof(long));
            if (byteCount == 0)
                throw new InvalidOperationException(
                    $"Column '{columnName}' requires {byteCount} bytes but encoded buffer capacity is {maxEncodedBytes}.");
            var destination = writer.GetSpan(byteCount);

            for (var i = 0; i < values.Length; i++)
            {
                var deltaTicks = checked(values[i].UtcTicks - Encoding.UnixEpochTicks);
                var unixMicros = deltaTicks / Encoding.TicksPerMicrosecond;
                BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(i * 8, 8), unixMicros);
            }

            writer.Advance(byteCount);
            state.EncodedLength = byteCount;
            state.UncompressedLength = byteCount;
        }

        internal static void EncodeTimeOnly(ReadOnlySpan<TimeOnly> values, ref DestinationBufferWriter writer,
            ref ParquetWriter.RowGroupState.ColumnState state, string columnName, int maxEncodedBytes)
        {
            var byteCount = checked(values.Length * sizeof(long));
            if (byteCount == 0)
                throw new InvalidOperationException(
                    $"Column '{columnName}' requires {byteCount} bytes but encoded buffer capacity is {maxEncodedBytes}.");
            var destination = writer.GetSpan(byteCount);

            for (var i = 0; i < values.Length; i++)
            {
                var micros = values[i].Ticks / Encoding.TicksPerMicrosecond;
                BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(i * 8, 8), micros);
            }

            writer.Advance(byteCount);
            state.EncodedLength = byteCount;
            state.UncompressedLength = byteCount;
        }

        internal static void EncodeString(ReadOnlySpan<string> values, ref DestinationBufferWriter writer,
            ref ParquetWriter.RowGroupState.ColumnState state, string columnName, int maxEncodedBytes)
        {
            var offset = 0;
            foreach (var value in values)
            {
                var nonNullValue = value ??
                                   throw new InvalidOperationException($"Column '{columnName}' does not support null values.");
                var byteCount = Encoding.Utf8.GetByteCount(nonNullValue);

                var lengthDestination = writer.GetSpan(sizeof(int));
                BinaryPrimitives.WriteInt32LittleEndian(lengthDestination, byteCount);
                writer.Advance(sizeof(int));

                if (byteCount == 0)
                {
                    offset += sizeof(int);
                    continue;
                }

                var valueDestination = writer.GetSpan(byteCount);
                var bytesWritten = Encoding.Utf8.GetBytes(nonNullValue.AsSpan(), valueDestination);
                if (bytesWritten != byteCount)
                    throw new InvalidOperationException($"Column '{columnName}' overflow while encoding UTF-8 payload.");

                writer.Advance(bytesWritten);
                offset += sizeof(int) + bytesWritten;
            }

            state.EncodedLength = offset;
            state.UncompressedLength = offset;
        }

        internal static void EncodeByteArray(ReadOnlySpan<byte[]> values, ref DestinationBufferWriter writer,
            ref ParquetWriter.RowGroupState.ColumnState state, string columnName, int maxEncodedBytes)
        {
            var offset = 0;
            foreach (var value in values)
            {
                var nonNullValue = value ??
                                   throw new InvalidOperationException($"Column '{columnName}' does not support null values.");
                var payloadLength = nonNullValue.Length;

                var lengthDestination = writer.GetSpan(sizeof(int));
                BinaryPrimitives.WriteInt32LittleEndian(lengthDestination, payloadLength);
                writer.Advance(sizeof(int));

                if (payloadLength == 0)
                {
                    offset += sizeof(int);
                    continue;
                }

                var valueDestination = writer.GetSpan(payloadLength);
                nonNullValue.AsSpan().CopyTo(valueDestination);
                writer.Advance(payloadLength);
                offset += sizeof(int) + payloadLength;
            }

            state.EncodedLength = offset;
            state.UncompressedLength = offset;
        }

        internal static void EncodeFloat(ReadOnlySpan<float> values, ref DestinationBufferWriter writer,
            ref ParquetWriter.RowGroupState.ColumnState state, string columnName, int maxEncodedBytes)
        {
            var byteCount = checked(values.Length * sizeof(float));
            if (byteCount == 0)
                throw new InvalidOperationException(
                    $"Column '{columnName}' requires {byteCount} bytes but encoded buffer capacity is {maxEncodedBytes}.");
            var destination = writer.GetSpan(byteCount);

            if (BitConverter.IsLittleEndian)
                MemoryMarshal.AsBytes(values).CopyTo(destination);
            else
                for (var i = 0; i < values.Length; i++)
                    BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(i * 4, 4),
                        BitConverter.SingleToInt32Bits(values[i]));

            writer.Advance(byteCount);
            state.EncodedLength = byteCount;
            state.UncompressedLength = byteCount;
        }

        internal static void EncodeDouble(ReadOnlySpan<double> values, ref DestinationBufferWriter writer,
            ref ParquetWriter.RowGroupState.ColumnState state, string columnName, int maxEncodedBytes)
        {
            var byteCount = checked(values.Length * sizeof(double));
            if (byteCount == 0)
                throw new InvalidOperationException(
                    $"Column '{columnName}' requires {byteCount} bytes but encoded buffer capacity is {maxEncodedBytes}.");
            var destination = writer.GetSpan(byteCount);

            if (BitConverter.IsLittleEndian)
                MemoryMarshal.AsBytes(values).CopyTo(destination);
            else
                for (var i = 0; i < values.Length; i++)
                    BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(i * 8, 8),
                        BitConverter.DoubleToInt64Bits(values[i]));

            writer.Advance(byteCount);
            state.EncodedLength = byteCount;
            state.UncompressedLength = byteCount;
        }
    }
}
