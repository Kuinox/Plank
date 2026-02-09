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
            var destination = ColumnCodec.GetDestination(ref state, byteCount);
            if (destination.IsEmpty)
                throw new InvalidOperationException(
                    $"Column '{columnName}' requires {byteCount} bytes but encoded buffer capacity is {maxEncodedBytes}.");

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
            var destination = ColumnCodec.GetDestination(ref state, byteCount);
            if (byteCount > 0 && destination.IsEmpty)
                throw new InvalidOperationException(
                    $"Column '{columnName}' requires {byteCount} bytes but encoded buffer capacity is {maxEncodedBytes}.");

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
            var destination = ColumnCodec.GetDestination(ref state, byteCount);
            if (destination.IsEmpty)
                throw new InvalidOperationException(
                    $"Column '{columnName}' requires {byteCount} bytes but encoded buffer capacity is {maxEncodedBytes}.");

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
            var destination = ColumnCodec.GetDestination(ref state, byteCount);
            if (destination.IsEmpty)
                throw new InvalidOperationException(
                    $"Column '{columnName}' requires {byteCount} bytes but encoded buffer capacity is {maxEncodedBytes}.");

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
            var destination = ColumnCodec.GetDestination(ref state, byteCount);
            if (destination.IsEmpty)
                throw new InvalidOperationException(
                    $"Column '{columnName}' requires {byteCount} bytes but encoded buffer capacity is {maxEncodedBytes}.");

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
            var destination = ColumnCodec.GetDestination(ref state, byteCount);
            if (destination.IsEmpty)
                throw new InvalidOperationException(
                    $"Column '{columnName}' requires {byteCount} bytes but encoded buffer capacity is {maxEncodedBytes}.");

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
            var destination = ColumnCodec.GetDestination(ref state, byteCount);
            if (destination.IsEmpty)
                throw new InvalidOperationException(
                    $"Column '{columnName}' requires {byteCount} bytes but encoded buffer capacity is {maxEncodedBytes}.");

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
            var byteCount = 0;
            foreach (var t in values)
            {
                var value = t ??
                            throw new InvalidOperationException($"Column '{columnName}' does not support null values.");
                byteCount = checked(byteCount + sizeof(int));
                byteCount = checked(byteCount + ColumnCodec.Utf8.GetByteCount(value));
            }

            var destination = ColumnCodec.GetDestination(ref state, byteCount);
            if (byteCount > 0 && destination.IsEmpty)
                throw new InvalidOperationException(
                    $"Column '{columnName}' requires {byteCount} bytes but encoded buffer capacity is {maxEncodedBytes}.");

            var offset = 0;
            foreach (var value in values)
            {
                var utf8Length = ColumnCodec.Utf8.GetByteCount(value);
                BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(offset, sizeof(int)), utf8Length);
                offset += sizeof(int);
                if (utf8Length == 0)
                    continue;

                var written = ColumnCodec.Utf8.GetBytes(value.AsSpan(), destination.Slice(offset, utf8Length));
                if (written != utf8Length)
                    throw new InvalidOperationException($"Column '{columnName}' could not encode UTF-8 payload.");
                offset += utf8Length;
            }

            state.EncodedLength = byteCount;
            state.UncompressedLength = byteCount;
        }

        internal static void EncodeByteArray(ReadOnlySpan<byte[]> values, ref ParquetWriter.RowGroupState.ColumnState state,
            string columnName, int maxEncodedBytes)
        {
            var byteCount = 0;
            foreach (var t in values)
            {
                var value = t ??
                            throw new InvalidOperationException($"Column '{columnName}' does not support null values.");
                byteCount = checked(byteCount + sizeof(int));
                byteCount = checked(byteCount + value.Length);
            }

            var destination = ColumnCodec.GetDestination(ref state, byteCount);
            if (byteCount > 0 && destination.IsEmpty)
                throw new InvalidOperationException(
                    $"Column '{columnName}' requires {byteCount} bytes but encoded buffer capacity is {maxEncodedBytes}.");

            var offset = 0;
            foreach (var value in values)
            {
                BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(offset, sizeof(int)), value.Length);
                offset += sizeof(int);
                if (value.Length == 0)
                    continue;

                value.AsSpan().CopyTo(destination.Slice(offset, value.Length));
                offset += value.Length;
            }

            state.EncodedLength = byteCount;
            state.UncompressedLength = byteCount;
        }

        internal static void EncodeFloat(ReadOnlySpan<float> values, ref ParquetWriter.RowGroupState.ColumnState state,
            string columnName, int maxEncodedBytes)
        {
            var byteCount = checked(values.Length * sizeof(float));
            var destination = ColumnCodec.GetDestination(ref state, byteCount);
            if (destination.IsEmpty)
                throw new InvalidOperationException(
                    $"Column '{columnName}' requires {byteCount} bytes but encoded buffer capacity is {maxEncodedBytes}.");

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
            var destination = ColumnCodec.GetDestination(ref state, byteCount);
            if (destination.IsEmpty)
                throw new InvalidOperationException(
                    $"Column '{columnName}' requires {byteCount} bytes but encoded buffer capacity is {maxEncodedBytes}.");

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
