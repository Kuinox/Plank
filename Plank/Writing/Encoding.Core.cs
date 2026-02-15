using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using Plank.Schema;

namespace Plank.Writing;

static partial class Encoding
{
    internal const long TicksPerMicrosecond = 10;
    internal static readonly long UnixEpochTicks = DateTime.UnixEpoch.Ticks;
    internal static readonly int UnixEpochDayNumber = DateOnly.FromDateTime(DateTime.UnixEpoch).DayNumber;
    internal static readonly System.Text.Encoding Utf8 = new UTF8Encoding(false, true);

    internal static bool TryGetFixedWidthBytes(ParquetPhysicalType physicalType, out int width)
    {
        switch (physicalType)
        {
            case ParquetPhysicalType.Boolean:
                width = 1;
                return true;
            case ParquetPhysicalType.Int32:
                width = 4;
                return true;
            case ParquetPhysicalType.Int64:
                width = 8;
                return true;
            case ParquetPhysicalType.Float:
                width = 4;
                return true;
            case ParquetPhysicalType.Double:
                width = 8;
                return true;
            default:
                width = 0;
                return false;
        }
    }

    internal static DestinationBufferWriter GetDestinationWriter(ref ParquetWriter.RowGroupState.ColumnState state,
        int maxEncodedBytes, string columnName)
    {
        if (maxEncodedBytes <= 0)
            throw new InvalidOperationException(
                $"Column '{columnName}' has no encoded buffer capacity.");

        if (state.EncodedBuffer is not null)
        {
            if (state.EncodedBuffer.Length < maxEncodedBytes)
                throw new InvalidOperationException(
                    $"Column '{columnName}' requires {maxEncodedBytes} bytes but encoded buffer capacity is {state.EncodedBuffer.Length}.");
            return new DestinationBufferWriter(state.EncodedBuffer.AsMemory(0, maxEncodedBytes));
        }

        if (state.EncodedBufferOwner is null)
            throw new InvalidOperationException($"Column '{columnName}' has no encoded buffer.");

        if (state.EncodedBufferOwner.Memory.Length < maxEncodedBytes)
            throw new InvalidOperationException(
                $"Column '{columnName}' requires {maxEncodedBytes} bytes but encoded buffer capacity is {state.EncodedBufferOwner.Memory.Length}.");

        return new DestinationBufferWriter(state.EncodedBufferOwner.Memory[..maxEncodedBytes]);
    }

    static VariableSizeBuffer CreateVariableSizeBuffer(ref ParquetWriter.RowGroupState.ColumnState state, int maxEncodedBytes,
        string columnName)
    {
        if (maxEncodedBytes <= 0)
            throw new InvalidOperationException(
                $"Column '{columnName}' has no encoded buffer capacity.");

        if (state.EncodedBuffer is not null)
        {
            if (state.EncodedBuffer.Length < maxEncodedBytes)
                throw new InvalidOperationException(
                    $"Column '{columnName}' requires {maxEncodedBytes} bytes but encoded buffer capacity is {state.EncodedBuffer.Length}.");
            return new VariableSizeBuffer(state.EncodedBuffer.AsMemory(0, maxEncodedBytes), columnName);
        }

        if (state.EncodedBufferOwner is null)
            throw new InvalidOperationException($"Column '{columnName}' has no encoded buffer.");

        if (state.EncodedBufferOwner.Memory.Length < maxEncodedBytes)
            throw new InvalidOperationException(
                $"Column '{columnName}' requires {maxEncodedBytes} bytes but encoded buffer capacity is {state.EncodedBufferOwner.Memory.Length}.");

        return new VariableSizeBuffer(state.EncodedBufferOwner.Memory[..maxEncodedBytes], columnName);
    }

    static void WriteInt32(VariableSizeBuffer writer, int value)
    {
        var destination = writer.GetSpan(sizeof(int));
        BinaryPrimitives.WriteInt32LittleEndian(destination, value);
        writer.Advance(sizeof(int));
    }

    static void WriteInt64(VariableSizeBuffer writer, long value)
    {
        var destination = writer.GetSpan(sizeof(long));
        BinaryPrimitives.WriteInt64LittleEndian(destination, value);
        writer.Advance(sizeof(long));
    }

    static void WriteStringPayload(VariableSizeBuffer writer, string value, string columnName)
    {
        var lengthOffset = writer._written;
        WriteInt32(writer, 0);

        var destination = writer.GetSpan();
        Utf8.GetEncoder().Convert(value.AsSpan(), destination, flush: true, out var charsUsed, out var bytesUsed,
            out var completed);
        if (!completed || charsUsed != value.Length)
            throw new InvalidOperationException($"Column '{columnName}' overflow while encoding UTF-8 payload.");

        writer.Advance(bytesUsed);
        writer.OverwriteInt32(lengthOffset, bytesUsed);
    }

    static void WriteByteArrayPayload(VariableSizeBuffer writer, byte[] value)
    {
        var payloadLength = value.Length;
        WriteInt32(writer, payloadLength);
        if (payloadLength == 0)
            return;

        var destination = writer.GetSpan(payloadLength);
        value.AsSpan().CopyTo(destination);
        writer.Advance(payloadLength);
    }

    internal static long ToUnixMicroseconds(DateTime value, DateTimeKindHandling handling, string columnName)
    {
        var normalized = NormalizeDateTime(value, handling, columnName);
        var deltaTicks = checked(normalized.Ticks - UnixEpochTicks);
        return deltaTicks / TicksPerMicrosecond;
    }

    static DateTime NormalizeDateTime(DateTime value, DateTimeKindHandling handling, string columnName)
    {
        return value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => NormalizeLocal(value, handling, columnName),
            DateTimeKind.Unspecified => NormalizeUnspecified(value, handling, columnName),
            _ => throw new NotSupportedException(
                $"DateTime kind '{value.Kind}' is not supported for column '{columnName}'.")
        };
    }

    static DateTime NormalizeLocal(DateTime value, DateTimeKindHandling handling, string columnName)
    {
        if ((handling & DateTimeKindHandling.PreserveClockTime) != 0)
            return DateTime.SpecifyKind(value, DateTimeKind.Utc);
        if ((handling & DateTimeKindHandling.ConvertLocalToUtc) != 0)
            return value.ToUniversalTime();
        if ((handling & DateTimeKindHandling.RequireUtc) != 0 || handling == DateTimeKindHandling.None)
            throw new InvalidOperationException(
                $"Column '{columnName}' received Local DateTime but policy requires UTC.");

        return value.ToUniversalTime();
    }

    static DateTime NormalizeUnspecified(DateTime value, DateTimeKindHandling handling, string columnName)
    {
        if ((handling & DateTimeKindHandling.PreserveClockTime) != 0)
            return DateTime.SpecifyKind(value, DateTimeKind.Utc);
        if ((handling & DateTimeKindHandling.AssumeUnspecifiedAsUtc) != 0)
            return DateTime.SpecifyKind(value, DateTimeKind.Utc);
        if ((handling & DateTimeKindHandling.RequireUtc) != 0 || handling == DateTimeKindHandling.None)
            throw new InvalidOperationException(
                $"Column '{columnName}' received Unspecified DateTime but policy requires UTC.");

        throw new InvalidOperationException(
            $"Column '{columnName}' received Unspecified DateTime and no policy was configured.");
    }

    internal static void ValidateDateTimeHandling(DateTimeKindHandling handling, string columnName)
    {
        if ((handling & DateTimeKindHandling.PreserveClockTime) == 0)
            return;
        if ((handling & (DateTimeKindHandling.ConvertLocalToUtc | DateTimeKindHandling.AssumeUnspecifiedAsUtc)) == 0)
            return;

        throw new InvalidOperationException(
            $"Column '{columnName}' has conflicting DateTime handling flags. PreserveClockTime cannot be combined with ConvertLocalToUtc or AssumeUnspecifiedAsUtc.");
    }

    static string GetUnsupportedTypeMessage(string columnName, ParquetPhysicalType physicalType)
        => physicalType switch
        {
            ParquetPhysicalType.Boolean => $"Column '{columnName}' expects Boolean values.",
            ParquetPhysicalType.Int32 => $"Column '{columnName}' expects Int32 values.",
            ParquetPhysicalType.Int64 => $"Column '{columnName}' expects Int64 values.",
            ParquetPhysicalType.ByteArray => $"Column '{columnName}' expects String or ByteArray values.",
            ParquetPhysicalType.Float => $"Column '{columnName}' expects Float values.",
            ParquetPhysicalType.Double => $"Column '{columnName}' expects Double values.",
            _ => $"Physical type '{physicalType}' is not supported."
        };
}
