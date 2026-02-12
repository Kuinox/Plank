using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Plank.Writing;

static partial class Encoding
{
    internal static class Plain
    {
        internal static void EncodeInt32(ReadOnlySpan<int> values, ref ParquetWriter.RowGroupState.ColumnState state,
            string columnName, int maxEncodedBytes)
        {
            var byteCount = checked(values.Length * sizeof(int));
            var destination = ColumnCodec.CreateFixedSizeBuffer(ref state, byteCount, maxEncodedBytes, columnName);

            if (BitConverter.IsLittleEndian)
                MemoryMarshal.AsBytes(values).CopyTo(destination);
            else
                for (var i = 0; i < values.Length; i++)
                    BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(i * 4, 4), values[i]);

            state.EncodedLength = byteCount;
            state.UncompressedLength = byteCount;
        }

        internal static void EncodeBoolean(ReadOnlySpan<bool> values, ref ParquetWriter.RowGroupState.ColumnState state,
            string columnName, int maxEncodedBytes)
        {
            var byteCount = (values.Length + 7) >> 3;
            var destination = ColumnCodec.CreateFixedSizeBuffer(ref state, byteCount, maxEncodedBytes, columnName);

            destination.Clear();
            for (var i = 0; i < values.Length; i++)
            {
                if (!values[i])
                    continue;

                var byteIndex = i >> 3;
                var bitIndex = i & 7;
                destination[byteIndex] |= (byte)(1 << bitIndex);
            }

            state.EncodedLength = byteCount;
            state.UncompressedLength = byteCount;
        }

        internal static void EncodeDateOnly(ReadOnlySpan<DateOnly> values, ref ParquetWriter.RowGroupState.ColumnState state,
            string columnName, int maxEncodedBytes)
        {
            var byteCount = checked(values.Length * sizeof(int));
            var destination = ColumnCodec.CreateFixedSizeBuffer(ref state, byteCount, maxEncodedBytes, columnName);

            for (var i = 0; i < values.Length; i++)
            {
                var daysSinceEpoch = checked(values[i].DayNumber - ColumnCodec.UnixEpochDayNumber);
                BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(i * 4, 4), daysSinceEpoch);
            }

            state.EncodedLength = byteCount;
            state.UncompressedLength = byteCount;
        }

        internal static void EncodeInt64(ReadOnlySpan<long> values, ref ParquetWriter.RowGroupState.ColumnState state,
            string columnName, int maxEncodedBytes)
        {
            var byteCount = checked(values.Length * sizeof(long));
            var destination = ColumnCodec.CreateFixedSizeBuffer(ref state, byteCount, maxEncodedBytes, columnName);

            if (BitConverter.IsLittleEndian)
                MemoryMarshal.AsBytes(values).CopyTo(destination);
            else
                for (var i = 0; i < values.Length; i++)
                    BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(i * 8, 8), values[i]);

            state.EncodedLength = byteCount;
            state.UncompressedLength = byteCount;
        }

        internal static void EncodeDateTime(ReadOnlySpan<DateTime> values, DateTimeKindHandling dateTimeKindHandling,
            ref ParquetWriter.RowGroupState.ColumnState state, string columnName, int maxEncodedBytes)
        {
            var byteCount = checked(values.Length * sizeof(long));
            var destination = ColumnCodec.CreateFixedSizeBuffer(ref state, byteCount, maxEncodedBytes, columnName);

            ColumnCodec.ValidateDateTimeHandling(dateTimeKindHandling, columnName);
            for (var i = 0; i < values.Length; i++)
            {
                var unixMicros = ColumnCodec.ToUnixMicroseconds(values[i], dateTimeKindHandling, columnName);
                BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(i * 8, 8), unixMicros);
            }

            state.EncodedLength = byteCount;
            state.UncompressedLength = byteCount;
        }

        internal static void EncodeDateTimeOffset(ReadOnlySpan<DateTimeOffset> values,
            ref ParquetWriter.RowGroupState.ColumnState state, string columnName, int maxEncodedBytes)
        {
            var byteCount = checked(values.Length * sizeof(long));
            var destination = ColumnCodec.CreateFixedSizeBuffer(ref state, byteCount, maxEncodedBytes, columnName);

            for (var i = 0; i < values.Length; i++)
            {
                var deltaTicks = checked(values[i].UtcTicks - ColumnCodec.UnixEpochTicks);
                var unixMicros = deltaTicks / ColumnCodec.TicksPerMicrosecond;
                BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(i * 8, 8), unixMicros);
            }

            state.EncodedLength = byteCount;
            state.UncompressedLength = byteCount;
        }

        internal static void EncodeTimeOnly(ReadOnlySpan<TimeOnly> values, ref ParquetWriter.RowGroupState.ColumnState state,
            string columnName, int maxEncodedBytes)
        {
            var byteCount = checked(values.Length * sizeof(long));
            var destination = ColumnCodec.CreateFixedSizeBuffer(ref state, byteCount, maxEncodedBytes, columnName);

            for (var i = 0; i < values.Length; i++)
            {
                var micros = values[i].Ticks / ColumnCodec.TicksPerMicrosecond;
                BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(i * 8, 8), micros);
            }

            state.EncodedLength = byteCount;
            state.UncompressedLength = byteCount;
        }

        internal static void EncodeString(ReadOnlySpan<string> values, ref ParquetWriter.RowGroupState.ColumnState state,
            string columnName, int maxEncodedBytes)
        {
            var destination = ColumnCodec.GetDestination(ref state, maxEncodedBytes);
            if (maxEncodedBytes > 0 && destination.IsEmpty)
                throw new InvalidOperationException(
                    $"Column '{columnName}' requires more than {maxEncodedBytes} bytes but encoded buffer capacity is {maxEncodedBytes}.");

            var offset = 0;
            foreach (var value in values)
            {
                var nonNullValue = value ??
                                   throw new InvalidOperationException($"Column '{columnName}' does not support null values.");
                if (offset > destination.Length - sizeof(int))
                    throw new InvalidOperationException($"Column '{columnName}' overflow while encoding UTF-8 payload.");

                var lengthOffset = offset;
                offset += sizeof(int);
                ColumnCodec.Utf8.GetEncoder().Convert(nonNullValue.AsSpan(), destination[offset..], flush: true,
                    out var charsUsed, out var bytesWritten, out var completed);
                if (!completed || charsUsed != nonNullValue.Length)
                    throw new InvalidOperationException($"Column '{columnName}' overflow while encoding UTF-8 payload.");
                offset = checked(offset + bytesWritten);
                BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(lengthOffset, sizeof(int)), bytesWritten);
            }

            state.EncodedLength = offset;
            state.UncompressedLength = offset;
        }

        internal static void EncodeByteArray(ReadOnlySpan<byte[]> values, ref ParquetWriter.RowGroupState.ColumnState state,
            string columnName, int maxEncodedBytes)
        {
            var destination = ColumnCodec.GetDestination(ref state, maxEncodedBytes);
            if (maxEncodedBytes > 0 && destination.IsEmpty)
                throw new InvalidOperationException(
                    $"Column '{columnName}' requires more than {maxEncodedBytes} bytes but encoded buffer capacity is {maxEncodedBytes}.");

            var offset = 0;
            foreach (var value in values)
            {
                var nonNullValue = value ??
                                   throw new InvalidOperationException($"Column '{columnName}' does not support null values.");
                if (offset > destination.Length - sizeof(int))
                    throw new InvalidOperationException($"Column '{columnName}' overflow while encoding byte-array payload.");

                BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(offset, sizeof(int)), nonNullValue.Length);
                offset += sizeof(int);
                if (nonNullValue.Length == 0)
                    continue;

                if (offset > destination.Length - nonNullValue.Length)
                    throw new InvalidOperationException($"Column '{columnName}' overflow while encoding byte-array payload.");

                nonNullValue.AsSpan().CopyTo(destination[offset..]);
                offset += nonNullValue.Length;
            }

            state.EncodedLength = offset;
            state.UncompressedLength = offset;
        }

        internal static void EncodeFloat(ReadOnlySpan<float> values, ref ParquetWriter.RowGroupState.ColumnState state,
            string columnName, int maxEncodedBytes)
        {
            var byteCount = checked(values.Length * sizeof(float));
            var destination = ColumnCodec.CreateFixedSizeBuffer(ref state, byteCount, maxEncodedBytes, columnName);

            if (BitConverter.IsLittleEndian)
                MemoryMarshal.AsBytes(values).CopyTo(destination);
            else
                for (var i = 0; i < values.Length; i++)
                    BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(i * 4, 4),
                        BitConverter.SingleToInt32Bits(values[i]));

            state.EncodedLength = byteCount;
            state.UncompressedLength = byteCount;
        }

        internal static void EncodeDouble(ReadOnlySpan<double> values, ref ParquetWriter.RowGroupState.ColumnState state,
            string columnName, int maxEncodedBytes)
        {
            var byteCount = checked(values.Length * sizeof(double));
            var destination = ColumnCodec.CreateFixedSizeBuffer(ref state, byteCount, maxEncodedBytes, columnName);

            if (BitConverter.IsLittleEndian)
                MemoryMarshal.AsBytes(values).CopyTo(destination);
            else
                for (var i = 0; i < values.Length; i++)
                    BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(i * 8, 8),
                        BitConverter.DoubleToInt64Bits(values[i]));

            state.EncodedLength = byteCount;
            state.UncompressedLength = byteCount;
        }
    }
}
