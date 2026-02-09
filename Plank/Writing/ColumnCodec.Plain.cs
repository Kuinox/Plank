using System.Buffers.Binary;
using System.Runtime.InteropServices;
namespace Plank.Writing;

static partial class ColumnCodec
{
    static void EncodePlainInt32(ReadOnlySpan<int> values, ref ParquetWriter.RowGroupState.ColumnState state,
        string columnName, int maxEncodedBytes)
    {
        var byteCount = checked(values.Length * sizeof(int));
        var destination = GetDestination(ref state, byteCount);
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

    static void EncodePlainBoolean(ReadOnlySpan<bool> values, ref ParquetWriter.RowGroupState.ColumnState state,
        string columnName, int maxEncodedBytes)
    {
        var byteCount = (values.Length + 7) >> 3;
        var destination = GetDestination(ref state, byteCount);
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

    static void EncodePlainDateOnly(ReadOnlySpan<DateOnly> values, ref ParquetWriter.RowGroupState.ColumnState state,
        string columnName, int maxEncodedBytes)
    {
        var byteCount = checked(values.Length * sizeof(int));
        var destination = GetDestination(ref state, byteCount);
        if (destination.IsEmpty)
            throw new InvalidOperationException(
                $"Column '{columnName}' requires {byteCount} bytes but encoded buffer capacity is {maxEncodedBytes}.");

        for (var i = 0; i < values.Length; i++)
        {
            var daysSinceEpoch = checked(values[i].DayNumber - UnixEpochDayNumber);
            BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(i * 4, 4), daysSinceEpoch);
        }

        state.EncodedLength = byteCount;
        state.UncompressedLength = byteCount;
    }

    static void EncodePlainInt64(ReadOnlySpan<long> values, ref ParquetWriter.RowGroupState.ColumnState state,
        string columnName, int maxEncodedBytes)
    {
        var byteCount = checked(values.Length * sizeof(long));
        var destination = GetDestination(ref state, byteCount);
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

    static void EncodePlainDateTime(ReadOnlySpan<DateTime> values, DateTimeKindHandling dateTimeKindHandling,
        ref ParquetWriter.RowGroupState.ColumnState state, string columnName, int maxEncodedBytes)
    {
        var byteCount = checked(values.Length * sizeof(long));
        var destination = GetDestination(ref state, byteCount);
        if (destination.IsEmpty)
            throw new InvalidOperationException(
                $"Column '{columnName}' requires {byteCount} bytes but encoded buffer capacity is {maxEncodedBytes}.");

        ValidateDateTimeHandling(dateTimeKindHandling, columnName);
        for (var i = 0; i < values.Length; i++)
        {
            var unixMicros = ToUnixMicroseconds(values[i], dateTimeKindHandling, columnName);
            BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(i * 8, 8), unixMicros);
        }

        state.EncodedLength = byteCount;
        state.UncompressedLength = byteCount;
    }

    static void EncodePlainDateTimeOffset(ReadOnlySpan<DateTimeOffset> values,
        ref ParquetWriter.RowGroupState.ColumnState state, string columnName, int maxEncodedBytes)
    {
        var byteCount = checked(values.Length * sizeof(long));
        var destination = GetDestination(ref state, byteCount);
        if (destination.IsEmpty)
            throw new InvalidOperationException(
                $"Column '{columnName}' requires {byteCount} bytes but encoded buffer capacity is {maxEncodedBytes}.");

        for (var i = 0; i < values.Length; i++)
        {
            var deltaTicks = checked(values[i].UtcTicks - UnixEpochTicks);
            var unixMicros = deltaTicks / TicksPerMicrosecond;
            BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(i * 8, 8), unixMicros);
        }

        state.EncodedLength = byteCount;
        state.UncompressedLength = byteCount;
    }

    static void EncodePlainTimeOnly(ReadOnlySpan<TimeOnly> values, ref ParquetWriter.RowGroupState.ColumnState state,
        string columnName, int maxEncodedBytes)
    {
        var byteCount = checked(values.Length * sizeof(long));
        var destination = GetDestination(ref state, byteCount);
        if (destination.IsEmpty)
            throw new InvalidOperationException(
                $"Column '{columnName}' requires {byteCount} bytes but encoded buffer capacity is {maxEncodedBytes}.");

        for (var i = 0; i < values.Length; i++)
        {
            var micros = values[i].Ticks / TicksPerMicrosecond;
            BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(i * 8, 8), micros);
        }

        state.EncodedLength = byteCount;
        state.UncompressedLength = byteCount;
    }

    static void EncodePlainString(ReadOnlySpan<string> values, ref ParquetWriter.RowGroupState.ColumnState state,
        string columnName, int maxEncodedBytes)
    {
        var byteCount = 0;
        foreach (var t in values)
        {
            var value = t ??
                        throw new InvalidOperationException($"Column '{columnName}' does not support null values.");
            byteCount = checked(byteCount + sizeof(int));
            byteCount = checked(byteCount + Utf8.GetByteCount(value));
        }

        var destination = GetDestination(ref state, byteCount);
        if (byteCount > 0 && destination.IsEmpty)
            throw new InvalidOperationException(
                $"Column '{columnName}' requires {byteCount} bytes but encoded buffer capacity is {maxEncodedBytes}.");

        var offset = 0;
        foreach (var value in values)
        {
            var utf8Length = Utf8.GetByteCount(value);
            BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(offset, sizeof(int)), utf8Length);
            offset += sizeof(int);
            if (utf8Length == 0)
                continue;

            var written = Utf8.GetBytes(value.AsSpan(), destination.Slice(offset, utf8Length));
            if (written != utf8Length)
                throw new InvalidOperationException($"Column '{columnName}' could not encode UTF-8 payload.");
            offset += utf8Length;
        }

        state.EncodedLength = byteCount;
        state.UncompressedLength = byteCount;
    }

    static void EncodePlainByteArray(ReadOnlySpan<byte[]> values, ref ParquetWriter.RowGroupState.ColumnState state,
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

        var destination = GetDestination(ref state, byteCount);
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

    static void EncodePlainFloat(ReadOnlySpan<float> values, ref ParquetWriter.RowGroupState.ColumnState state,
        string columnName, int maxEncodedBytes)
    {
        var byteCount = checked(values.Length * sizeof(float));
        var destination = GetDestination(ref state, byteCount);
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

    static void EncodePlainDouble(ReadOnlySpan<double> values, ref ParquetWriter.RowGroupState.ColumnState state,
        string columnName, int maxEncodedBytes)
    {
        var byteCount = checked(values.Length * sizeof(double));
        var destination = GetDestination(ref state, byteCount);
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
