using System.Runtime.InteropServices;

namespace Plank.Writing;

static partial class Encoding
{
    internal static class ByteStreamSplit
    {
        internal static void EncodeInt32(ReadOnlySpan<int> values, ref ParquetWriter.RowGroupState.ColumnState state,
            string columnName, int maxEncodedBytes)
            => EncodeFixed(values.Length, sizeof(int), ref state, columnName, maxEncodedBytes,
                static (row, lane) => (byte)(row >> (lane * 8)), values);

        internal static void EncodeInt64(ReadOnlySpan<long> values, ref ParquetWriter.RowGroupState.ColumnState state,
            string columnName, int maxEncodedBytes)
            => EncodeFixed(values.Length, sizeof(long), ref state, columnName, maxEncodedBytes,
                static (row, lane) => (byte)(row >> (lane * 8)), values);

        internal static void EncodeFloat(ReadOnlySpan<float> values, ref ParquetWriter.RowGroupState.ColumnState state,
            string columnName, int maxEncodedBytes)
            => EncodeFixed(values.Length, sizeof(int), ref state, columnName, maxEncodedBytes,
                static (row, lane) => (byte)(row >> (lane * 8)), Reinterpret<float, int>(values));

        internal static void EncodeDouble(ReadOnlySpan<double> values, ref ParquetWriter.RowGroupState.ColumnState state,
            string columnName, int maxEncodedBytes)
            => EncodeFixed(values.Length, sizeof(long), ref state, columnName, maxEncodedBytes,
                static (row, lane) => (byte)(row >> (lane * 8)), Reinterpret<double, long>(values));

        static ReadOnlySpan<TTo> Reinterpret<TFrom, TTo>(ReadOnlySpan<TFrom> values)
            where TFrom : struct
            where TTo : struct
            => MemoryMarshal.Cast<TFrom, TTo>(values);

        static void EncodeFixed<T>(int valueCount, int width, ref ParquetWriter.RowGroupState.ColumnState state,
            string columnName, int maxEncodedBytes, Func<T, int, byte> laneReader, ReadOnlySpan<T> rows = default)
            where T : struct
        {
            var byteCount = checked(valueCount * width);
            var destination = ColumnCodec.GetDestination(ref state, byteCount);
            if (byteCount > 0 && destination.IsEmpty)
                throw new InvalidOperationException(
                    $"Column '{columnName}' requires {byteCount} bytes but encoded buffer capacity is {maxEncodedBytes}.");

            for (var lane = 0; lane < width; lane++)
            for (var i = 0; i < valueCount; i++)
                destination[(lane * valueCount) + i] = laneReader(rows[i], lane);

            state.EncodedLength = byteCount;
            state.UncompressedLength = byteCount;
        }

    }

    internal static class DeltaBinaryPacked
    {
        const int BlockSize = 128;
        const int MiniBlockCount = 4;
        const int MiniBlockSize = BlockSize / MiniBlockCount;

        internal static void EncodeInt32(ReadOnlySpan<int> values, ref ParquetWriter.RowGroupState.ColumnState state,
            string columnName, int maxEncodedBytes)
        {
            var destination = ColumnCodec.GetDestination(ref state, maxEncodedBytes);
            if (maxEncodedBytes > 0 && destination.IsEmpty)
                throw new InvalidOperationException(
                    $"Column '{columnName}' requires more than {maxEncodedBytes} bytes but encoded buffer capacity is {maxEncodedBytes}.");

            var written = WriteInt32(values, destination, columnName);
            state.EncodedLength = written;
            state.UncompressedLength = written;
        }

        internal static void EncodeInt64(ReadOnlySpan<long> values, ref ParquetWriter.RowGroupState.ColumnState state,
            string columnName, int maxEncodedBytes)
        {
            var destination = ColumnCodec.GetDestination(ref state, maxEncodedBytes);
            if (maxEncodedBytes > 0 && destination.IsEmpty)
                throw new InvalidOperationException(
                    $"Column '{columnName}' requires more than {maxEncodedBytes} bytes but encoded buffer capacity is {maxEncodedBytes}.");

            var written = WriteInt64(values, destination, columnName);
            state.EncodedLength = written;
            state.UncompressedLength = written;
        }

        internal static int WriteInt32(ReadOnlySpan<int> values, Span<byte> destination, string columnName)
        {
            if (values.Length == 0)
                return WriteHeaderAndFirst(0, 0, destination, 0, out _);

            var offset = WriteHeaderAndFirst(values.Length, ZigZag32(values[0]), destination, 0, out var firstOut);
            if (firstOut < 0)
                throw new InvalidOperationException($"Column '{columnName}' could not encode DELTA_BINARY_PACKED header.");
            if (values.Length == 1)
                return offset;

            Span<long> blockDeltas = stackalloc long[BlockSize];
            var previous = values[0];
            var index = 1;
            while (index < values.Length)
            {
                var count = Math.Min(BlockSize, values.Length - index);
                var minDelta = long.MaxValue;
                for (var i = 0; i < count; i++)
                {
                    var current = values[index + i];
                    var delta = (long)current - previous;
                    previous = current;
                    blockDeltas[i] = delta;
                    if (delta < minDelta)
                        minDelta = delta;
                }
                for (var i = count; i < BlockSize; i++)
                    blockDeltas[i] = minDelta;

                offset = WriteBlock(destination, offset, blockDeltas, minDelta, columnName);
                index += count;
            }

            return offset;
        }

        internal static int WriteInt64(ReadOnlySpan<long> values, Span<byte> destination, string columnName)
        {
            if (values.Length == 0)
                return WriteHeaderAndFirst(0, 0, destination, 0, out _);

            var offset = WriteHeaderAndFirst(values.Length, ZigZag64(values[0]), destination, 0, out var firstOut);
            if (firstOut < 0)
                throw new InvalidOperationException($"Column '{columnName}' could not encode DELTA_BINARY_PACKED header.");
            if (values.Length == 1)
                return offset;

            Span<long> blockDeltas = stackalloc long[BlockSize];
            var previous = values[0];
            var index = 1;
            while (index < values.Length)
            {
                var count = Math.Min(BlockSize, values.Length - index);
                var minDelta = long.MaxValue;
                for (var i = 0; i < count; i++)
                {
                    var current = values[index + i];
                    var delta = current - previous;
                    previous = current;
                    blockDeltas[i] = delta;
                    if (delta < minDelta)
                        minDelta = delta;
                }
                for (var i = count; i < BlockSize; i++)
                    blockDeltas[i] = minDelta;

                offset = WriteBlock(destination, offset, blockDeltas, minDelta, columnName);
                index += count;
            }

            return offset;
        }

        static int WriteBlock(Span<byte> destination, int offset, Span<long> deltas, long minDelta, string columnName)
        {
            offset = WriteVarUInt64(destination, offset, ZigZag64(minDelta), columnName);

            Span<byte> bitWidths = stackalloc byte[MiniBlockCount];
            for (var block = 0; block < MiniBlockCount; block++)
            {
                var start = block * MiniBlockSize;
                ulong max = 0;
                for (var i = 0; i < MiniBlockSize; i++)
                {
                    var normalized = (ulong)(deltas[start + i] - minDelta);
                    if (normalized > max)
                        max = normalized;
                    deltas[start + i] = (long)normalized;
                }
                bitWidths[block] = GetBitWidth(max);
            }

            if (destination.Length - offset < MiniBlockCount)
                throw new InvalidOperationException($"Column '{columnName}' overflow while writing DELTA_BINARY_PACKED bit widths.");
            bitWidths.CopyTo(destination[offset..]);
            offset += MiniBlockCount;

            for (var block = 0; block < MiniBlockCount; block++)
            {
                var width = bitWidths[block];
                offset = PackMiniBlock(deltas[(block * MiniBlockSize)..((block + 1) * MiniBlockSize)], width, destination,
                    offset, columnName);
            }

            return offset;
        }

        static int PackMiniBlock(ReadOnlySpan<long> values, int bitWidth, Span<byte> destination, int offset,
            string columnName)
        {
            if (bitWidth == 0)
                return offset;

            var bitBuffer = 0UL;
            var bitCount = 0;
            var mask = bitWidth == 64 ? ulong.MaxValue : ((1UL << bitWidth) - 1);
            foreach (var raw in values)
            {
                var value = (ulong)raw & mask;
                bitBuffer |= value << bitCount;
                bitCount += bitWidth;
                while (bitCount >= 8)
                {
                    if (offset >= destination.Length)
                        throw new InvalidOperationException($"Column '{columnName}' overflow while writing DELTA_BINARY_PACKED miniblock.");
                    destination[offset++] = (byte)bitBuffer;
                    bitBuffer >>= 8;
                    bitCount -= 8;
                }
            }

            if (bitCount <= 0)
                return offset;
            if (offset >= destination.Length)
                throw new InvalidOperationException($"Column '{columnName}' overflow while finalizing DELTA_BINARY_PACKED miniblock.");
            destination[offset++] = (byte)bitBuffer;
            return offset;
        }

        static int WriteHeaderAndFirst(int totalValueCount, ulong firstValueZigZag, Span<byte> destination, int offset,
            out int finalOffset)
        {
            offset = WriteVarUInt64(destination, offset, BlockSize, "");
            offset = WriteVarUInt64(destination, offset, MiniBlockCount, "");
            offset = WriteVarUInt64(destination, offset, (ulong)totalValueCount, "");
            offset = WriteVarUInt64(destination, offset, firstValueZigZag, "");
            finalOffset = offset;
            return offset;
        }

        static int WriteVarUInt64(Span<byte> destination, int offset, ulong value, string columnName)
        {
            while (value >= 0x80)
            {
                if (offset >= destination.Length)
                    throw new InvalidOperationException($"Column '{columnName}' overflow while writing varint.");
                destination[offset++] = (byte)(value | 0x80);
                value >>= 7;
            }

            if (offset >= destination.Length)
                throw new InvalidOperationException($"Column '{columnName}' overflow while writing varint.");
            destination[offset++] = (byte)value;
            return offset;
        }

        static byte GetBitWidth(ulong value)
        {
            if (value == 0)
                return 0;
            byte width = 0;
            while (value != 0)
            {
                width++;
                value >>= 1;
            }
            return width;
        }

        static ulong ZigZag32(int value)
            => (uint)((value << 1) ^ (value >> 31));

        static ulong ZigZag64(long value)
            => (ulong)((value << 1) ^ (value >> 63));
    }

    internal static class DeltaLengthByteArray
    {
        internal static void EncodeString(ReadOnlySpan<string> values, ref ParquetWriter.RowGroupState.ColumnState state,
            string columnName, int maxEncodedBytes)
        {
            var destination = ColumnCodec.GetDestination(ref state, maxEncodedBytes);
            if (maxEncodedBytes > 0 && destination.IsEmpty)
                throw new InvalidOperationException(
                    $"Column '{columnName}' requires more than {maxEncodedBytes} bytes but encoded buffer capacity is {maxEncodedBytes}.");

            var lengths = new int[values.Length];
            var totalPayload = 0;
            for (var i = 0; i < values.Length; i++)
            {
                var value = values[i] ?? throw new InvalidOperationException($"Column '{columnName}' does not support null values.");
                var len = ColumnCodec.Utf8.GetByteCount(value);
                lengths[i] = len;
                totalPayload = checked(totalPayload + len);
            }

            var offset = DeltaBinaryPacked.WriteInt32(lengths, destination, columnName);
            for (var i = 0; i < values.Length; i++)
                offset += ColumnCodec.Utf8.GetBytes(values[i].AsSpan(), destination[offset..]);

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

            var lengths = new int[values.Length];
            for (var i = 0; i < values.Length; i++)
            {
                var value = values[i] ?? throw new InvalidOperationException($"Column '{columnName}' does not support null values.");
                lengths[i] = value.Length;
            }

            var offset = DeltaBinaryPacked.WriteInt32(lengths, destination, columnName);
            for (var i = 0; i < values.Length; i++)
            {
                var value = values[i];
                value.AsSpan().CopyTo(destination[offset..]);
                offset += value.Length;
            }

            state.EncodedLength = offset;
            state.UncompressedLength = offset;
        }
    }

    internal static class DeltaByteArray
    {
        internal static void EncodeString(ReadOnlySpan<string> values, ref ParquetWriter.RowGroupState.ColumnState state,
            string columnName, int maxEncodedBytes)
        {
            var encoded = new byte[values.Length][];
            for (var i = 0; i < values.Length; i++)
            {
                var value = values[i] ?? throw new InvalidOperationException($"Column '{columnName}' does not support null values.");
                encoded[i] = ColumnCodec.Utf8.GetBytes(value);
            }

            EncodeByteArraysCore(encoded, ref state, columnName, maxEncodedBytes);
        }

        internal static void EncodeByteArray(ReadOnlySpan<byte[]> values, ref ParquetWriter.RowGroupState.ColumnState state,
            string columnName, int maxEncodedBytes)
        {
            var encoded = new byte[values.Length][];
            for (var i = 0; i < values.Length; i++)
            {
                var value = values[i] ?? throw new InvalidOperationException($"Column '{columnName}' does not support null values.");
                encoded[i] = value;
            }

            EncodeByteArraysCore(encoded, ref state, columnName, maxEncodedBytes);
        }

        static void EncodeByteArraysCore(ReadOnlySpan<byte[]> values, ref ParquetWriter.RowGroupState.ColumnState state,
            string columnName, int maxEncodedBytes)
        {
            var destination = ColumnCodec.GetDestination(ref state, maxEncodedBytes);
            if (maxEncodedBytes > 0 && destination.IsEmpty)
                throw new InvalidOperationException(
                    $"Column '{columnName}' requires more than {maxEncodedBytes} bytes but encoded buffer capacity is {maxEncodedBytes}.");

            var prefixLengths = new int[values.Length];
            var suffixLengths = new int[values.Length];
            var totalSuffixBytes = 0;
            var previous = Array.Empty<byte>();
            for (var i = 0; i < values.Length; i++)
            {
                var current = values[i];
                var prefix = SharedPrefixLength(previous, current);
                var suffix = current.Length - prefix;
                prefixLengths[i] = prefix;
                suffixLengths[i] = suffix;
                totalSuffixBytes = checked(totalSuffixBytes + suffix);
                previous = current;
            }

            var offset = DeltaBinaryPacked.WriteInt32(prefixLengths, destination, columnName);
            offset += DeltaBinaryPacked.WriteInt32(suffixLengths, destination[offset..], columnName);
            previous = Array.Empty<byte>();
            for (var i = 0; i < values.Length; i++)
            {
                var current = values[i];
                var prefix = prefixLengths[i];
                var suffix = suffixLengths[i];
                if (suffix > 0)
                {
                    current.AsSpan(prefix, suffix).CopyTo(destination[offset..]);
                    offset += suffix;
                }
                previous = current;
            }

            state.EncodedLength = offset;
            state.UncompressedLength = offset;
        }

        static int SharedPrefixLength(ReadOnlySpan<byte> previous, ReadOnlySpan<byte> current)
        {
            var max = Math.Min(previous.Length, current.Length);
            var count = 0;
            while (count < max && previous[count] == current[count])
                count++;
            return count;
        }
    }
}
