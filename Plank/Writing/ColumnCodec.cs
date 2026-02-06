using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Plank.Schema;

namespace Plank.Writing;

static class ColumnCodec
{
    const long TicksPerMicrosecond = 10;
    static readonly long UnixEpochTicks = DateTime.UnixEpoch.Ticks;
    static readonly int UnixEpochDayNumber = DateOnly.FromDateTime(DateTime.UnixEpoch).DayNumber;
    static readonly Encoding Utf8 = new UTF8Encoding(false, true);
    
    internal static void Encode<T>(Column column, ReadOnlySpan<T> values, ParquetPhysicalType physicalType, RowGroupOptions options, DateTimeKindHandling dateTimeKindHandling, ref ParquetWriter.RowGroupState.ColumnState state)
    {
        var encoding = ResolveDefaultEncoding(column.Options.Encodings);
        if (encoding != EncodingKind.Plain)
            throw new NotSupportedException($"Encoding '{encoding}' is not supported for column '{column.Name}'.");

        state.Encoding = encoding;
        var valueKind = ColumnDispatch.GetValueKind<T>();
        var dispatchKey = ColumnDispatch.GetDispatchKey(physicalType, valueKind);
        if (column.Options.Repetition is ParquetRepetition.Optional)
        {
            if (TryEncodeOptional(column, values, dispatchKey, options, dateTimeKindHandling, ref state))
                return;

            throw new NotSupportedException($"Optional column '{column.Name}' is not supported for value type '{typeof(T)}' yet.");
        }

        switch (dispatchKey)
        {
            case ColumnDispatch.DispatchKey.BooleanBool:
                EncodePlainBoolean(Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<bool>>(ref values), ref state, column.Name, options.MaxEncodedBytes);
                break;
            case ColumnDispatch.DispatchKey.Int32Int32:
                EncodePlainInt32(Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<int>>(ref values), ref state, column.Name, options.MaxEncodedBytes);
                break;
            case ColumnDispatch.DispatchKey.Int32DateOnly:
                EncodePlainDateOnly(Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<DateOnly>>(ref values), ref state, column.Name, options.MaxEncodedBytes);
                break;
            case ColumnDispatch.DispatchKey.Int64Int64:
                EncodePlainInt64(Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<long>>(ref values), ref state, column.Name, options.MaxEncodedBytes);
                break;
            case ColumnDispatch.DispatchKey.Int64DateTime:
                EncodePlainDateTime(Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<DateTime>>(ref values), dateTimeKindHandling, ref state, column.Name, options.MaxEncodedBytes);
                break;
            case ColumnDispatch.DispatchKey.Int64DateTimeOffset:
                EncodePlainDateTimeOffset(Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<DateTimeOffset>>(ref values), ref state, column.Name, options.MaxEncodedBytes);
                break;
            case ColumnDispatch.DispatchKey.Int64TimeOnly:
                EncodePlainTimeOnly(Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<TimeOnly>>(ref values), ref state, column.Name, options.MaxEncodedBytes);
                break;
            case ColumnDispatch.DispatchKey.ByteArrayString:
                EncodePlainString(Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<string>>(ref values), ref state, column.Name, options.MaxEncodedBytes);
                break;
            case ColumnDispatch.DispatchKey.ByteArrayByteArray:
                EncodePlainByteArray(Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<byte[]>>(ref values), ref state, column.Name, options.MaxEncodedBytes);
                break;
            case ColumnDispatch.DispatchKey.FloatFloat:
                EncodePlainFloat(Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<float>>(ref values), ref state, column.Name, options.MaxEncodedBytes);
                break;
            case ColumnDispatch.DispatchKey.DoubleDouble:
                EncodePlainDouble(Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<double>>(ref values), ref state, column.Name, options.MaxEncodedBytes);
                break;
            default:
                throw new InvalidOperationException(GetUnsupportedTypeMessage(column.Name, physicalType));
        }

        state.NullCount = 0;
        state.DefinitionLevelsByteLength = 0;
        state.RepetitionLevelsByteLength = 0;
    }

    internal static void EncodeRepeated<T>(Column column, ReadOnlySpan<T[]> rows, ParquetPhysicalType physicalType, RowGroupOptions options, DateTimeKindHandling dateTimeKindHandling, ref ParquetWriter.RowGroupState.ColumnState state)
    {
        if (column.Options.Repetition is not ParquetRepetition.Repeated)
            throw new InvalidOperationException($"Column '{column.Name}' is not configured as Repeated.");

        var encoding = ResolveDefaultEncoding(column.Options.Encodings);
        if (encoding != EncodingKind.Plain)
            throw new NotSupportedException($"Encoding '{encoding}' is not supported for column '{column.Name}'.");

        state.Encoding = encoding;
        var valueKind = ColumnDispatch.GetValueKind<T>();
        var dispatchKey = ColumnDispatch.GetDispatchKey(physicalType, valueKind);
        switch (dispatchKey)
        {
            case ColumnDispatch.DispatchKey.BooleanBool:
                EncodeRepeatedBoolean(Unsafe.As<ReadOnlySpan<T[]>, ReadOnlySpan<bool[]>>(ref rows), ref state, column.Name, options.MaxEncodedBytes);
                break;
            case ColumnDispatch.DispatchKey.Int32Int32:
                EncodeRepeatedInt32(Unsafe.As<ReadOnlySpan<T[]>, ReadOnlySpan<int[]>>(ref rows), ref state, column.Name, options.MaxEncodedBytes);
                break;
            case ColumnDispatch.DispatchKey.Int32NullableInt32:
                EncodeRepeatedNullableInt32(Unsafe.As<ReadOnlySpan<T[]>, ReadOnlySpan<int?[]>>(ref rows), ref state, column.Name, options.MaxEncodedBytes);
                break;
            case ColumnDispatch.DispatchKey.Int32DateOnly:
                EncodeRepeatedDateOnly(Unsafe.As<ReadOnlySpan<T[]>, ReadOnlySpan<DateOnly[]>>(ref rows), ref state, column.Name, options.MaxEncodedBytes);
                break;
            case ColumnDispatch.DispatchKey.Int64Int64:
                EncodeRepeatedInt64(Unsafe.As<ReadOnlySpan<T[]>, ReadOnlySpan<long[]>>(ref rows), ref state, column.Name, options.MaxEncodedBytes);
                break;
            case ColumnDispatch.DispatchKey.Int64DateTime:
                EncodeRepeatedDateTime(Unsafe.As<ReadOnlySpan<T[]>, ReadOnlySpan<DateTime[]>>(ref rows), dateTimeKindHandling, ref state, column.Name, options.MaxEncodedBytes);
                break;
            case ColumnDispatch.DispatchKey.Int64DateTimeOffset:
                EncodeRepeatedDateTimeOffset(Unsafe.As<ReadOnlySpan<T[]>, ReadOnlySpan<DateTimeOffset[]>>(ref rows), ref state, column.Name, options.MaxEncodedBytes);
                break;
            case ColumnDispatch.DispatchKey.Int64TimeOnly:
                EncodeRepeatedTimeOnly(Unsafe.As<ReadOnlySpan<T[]>, ReadOnlySpan<TimeOnly[]>>(ref rows), ref state, column.Name, options.MaxEncodedBytes);
                break;
            case ColumnDispatch.DispatchKey.ByteArrayString:
                EncodeRepeatedString(Unsafe.As<ReadOnlySpan<T[]>, ReadOnlySpan<string[]>>(ref rows), ref state, column.Name, options.MaxEncodedBytes);
                break;
            case ColumnDispatch.DispatchKey.ByteArrayByteArray:
                EncodeRepeatedByteArray(Unsafe.As<ReadOnlySpan<T[]>, ReadOnlySpan<byte[][]>>(ref rows), ref state, column.Name, options.MaxEncodedBytes);
                break;
            case ColumnDispatch.DispatchKey.FloatFloat:
                EncodeRepeatedFloat(Unsafe.As<ReadOnlySpan<T[]>, ReadOnlySpan<float[]>>(ref rows), ref state, column.Name, options.MaxEncodedBytes);
                break;
            case ColumnDispatch.DispatchKey.DoubleDouble:
                EncodeRepeatedDouble(Unsafe.As<ReadOnlySpan<T[]>, ReadOnlySpan<double[]>>(ref rows), ref state, column.Name, options.MaxEncodedBytes);
                break;
            default:
                throw new InvalidOperationException(GetUnsupportedTypeMessage(column.Name, physicalType));
        }
    }

    internal static void Compress(ref ParquetWriter.RowGroupState.ColumnState state, CompressionKind compression)
        => state.Compression = compression;

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

    static EncodingKind ResolveDefaultEncoding(ImmutableArray<EncodingKind> encodings)
        => encodings.IsDefaultOrEmpty ? EncodingKind.Plain : encodings[0];

    static void EncodePlainInt32(ReadOnlySpan<int> values, ref ParquetWriter.RowGroupState.ColumnState state, string columnName, int maxEncodedBytes)
    {
        var byteCount = checked(values.Length * sizeof(int));
        var destination = GetDestination(ref state, byteCount);
        if (destination.IsEmpty)
            throw new InvalidOperationException($"Column '{columnName}' requires {byteCount} bytes but MaxEncodedBytes is {maxEncodedBytes}.");

        if (BitConverter.IsLittleEndian)
            MemoryMarshal.AsBytes(values).CopyTo(destination);
        else
        {
            for (var i = 0; i < values.Length; i++)
                BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(i * 4, 4), values[i]);
        }

        state.EncodedLength = byteCount;
        state.UncompressedLength = byteCount;
    }

    static void EncodePlainBoolean(ReadOnlySpan<bool> values, ref ParquetWriter.RowGroupState.ColumnState state, string columnName, int maxEncodedBytes)
    {
        var byteCount = (values.Length + 7) >> 3;
        var destination = GetDestination(ref state, byteCount);
        if (byteCount > 0 && destination.IsEmpty)
            throw new InvalidOperationException($"Column '{columnName}' requires {byteCount} bytes but MaxEncodedBytes is {maxEncodedBytes}.");

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

    static void EncodePlainDateOnly(ReadOnlySpan<DateOnly> values, ref ParquetWriter.RowGroupState.ColumnState state, string columnName, int maxEncodedBytes)
    {
        var byteCount = checked(values.Length * sizeof(int));
        var destination = GetDestination(ref state, byteCount);
        if (destination.IsEmpty)
            throw new InvalidOperationException($"Column '{columnName}' requires {byteCount} bytes but MaxEncodedBytes is {maxEncodedBytes}.");

        for (var i = 0; i < values.Length; i++)
        {
            var daysSinceEpoch = checked(values[i].DayNumber - UnixEpochDayNumber);
            BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(i * 4, 4), daysSinceEpoch);
        }

        state.EncodedLength = byteCount;
        state.UncompressedLength = byteCount;
    }

    static void EncodePlainInt64(ReadOnlySpan<long> values, ref ParquetWriter.RowGroupState.ColumnState state, string columnName, int maxEncodedBytes)
    {
        var byteCount = checked(values.Length * sizeof(long));
        var destination = GetDestination(ref state, byteCount);
        if (destination.IsEmpty)
            throw new InvalidOperationException($"Column '{columnName}' requires {byteCount} bytes but MaxEncodedBytes is {maxEncodedBytes}.");

        if (BitConverter.IsLittleEndian)
            MemoryMarshal.AsBytes(values).CopyTo(destination);
        else
        {
            for (var i = 0; i < values.Length; i++)
                BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(i * 8, 8), values[i]);
        }

        state.EncodedLength = byteCount;
        state.UncompressedLength = byteCount;
    }

    static void EncodePlainDateTime(ReadOnlySpan<DateTime> values, DateTimeKindHandling dateTimeKindHandling, ref ParquetWriter.RowGroupState.ColumnState state, string columnName, int maxEncodedBytes)
    {
        var byteCount = checked(values.Length * sizeof(long));
        var destination = GetDestination(ref state, byteCount);
        if (destination.IsEmpty)
            throw new InvalidOperationException($"Column '{columnName}' requires {byteCount} bytes but MaxEncodedBytes is {maxEncodedBytes}.");

        ValidateDateTimeHandling(dateTimeKindHandling, columnName);
        for (var i = 0; i < values.Length; i++)
        {
            var unixMicros = ToUnixMicroseconds(values[i], dateTimeKindHandling, columnName);
            BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(i * 8, 8), unixMicros);
        }

        state.EncodedLength = byteCount;
        state.UncompressedLength = byteCount;
    }

    static void EncodePlainDateTimeOffset(ReadOnlySpan<DateTimeOffset> values, ref ParquetWriter.RowGroupState.ColumnState state, string columnName, int maxEncodedBytes)
    {
        var byteCount = checked(values.Length * sizeof(long));
        var destination = GetDestination(ref state, byteCount);
        if (destination.IsEmpty)
            throw new InvalidOperationException($"Column '{columnName}' requires {byteCount} bytes but MaxEncodedBytes is {maxEncodedBytes}.");

        for (var i = 0; i < values.Length; i++)
        {
            var deltaTicks = checked(values[i].UtcTicks - UnixEpochTicks);
            var unixMicros = deltaTicks / TicksPerMicrosecond;
            BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(i * 8, 8), unixMicros);
        }

        state.EncodedLength = byteCount;
        state.UncompressedLength = byteCount;
    }

    static void EncodePlainTimeOnly(ReadOnlySpan<TimeOnly> values, ref ParquetWriter.RowGroupState.ColumnState state, string columnName, int maxEncodedBytes)
    {
        var byteCount = checked(values.Length * sizeof(long));
        var destination = GetDestination(ref state, byteCount);
        if (destination.IsEmpty)
            throw new InvalidOperationException($"Column '{columnName}' requires {byteCount} bytes but MaxEncodedBytes is {maxEncodedBytes}.");

        for (var i = 0; i < values.Length; i++)
        {
            var micros = values[i].Ticks / TicksPerMicrosecond;
            BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(i * 8, 8), micros);
        }

        state.EncodedLength = byteCount;
        state.UncompressedLength = byteCount;
    }

    static void EncodePlainString(ReadOnlySpan<string> values, ref ParquetWriter.RowGroupState.ColumnState state, string columnName, int maxEncodedBytes)
    {
        var byteCount = 0;
        for (var i = 0; i < values.Length; i++)
        {
            var value = values[i] ?? throw new InvalidOperationException($"Column '{columnName}' does not support null values.");
            byteCount = checked(byteCount + sizeof(int));
            byteCount = checked(byteCount + Utf8.GetByteCount(value));
        }

        var destination = GetDestination(ref state, byteCount);
        if (byteCount > 0 && destination.IsEmpty)
            throw new InvalidOperationException($"Column '{columnName}' requires {byteCount} bytes but MaxEncodedBytes is {maxEncodedBytes}.");

        var offset = 0;
        for (var i = 0; i < values.Length; i++)
        {
            var value = values[i];
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

    static void EncodePlainByteArray(ReadOnlySpan<byte[]> values, ref ParquetWriter.RowGroupState.ColumnState state, string columnName, int maxEncodedBytes)
    {
        var byteCount = 0;
        for (var i = 0; i < values.Length; i++)
        {
            var value = values[i] ?? throw new InvalidOperationException($"Column '{columnName}' does not support null values.");
            byteCount = checked(byteCount + sizeof(int));
            byteCount = checked(byteCount + value.Length);
        }

        var destination = GetDestination(ref state, byteCount);
        if (byteCount > 0 && destination.IsEmpty)
            throw new InvalidOperationException($"Column '{columnName}' requires {byteCount} bytes but MaxEncodedBytes is {maxEncodedBytes}.");

        var offset = 0;
        for (var i = 0; i < values.Length; i++)
        {
            var value = values[i];
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

    static void EncodePlainFloat(ReadOnlySpan<float> values, ref ParquetWriter.RowGroupState.ColumnState state, string columnName, int maxEncodedBytes)
    {
        var byteCount = checked(values.Length * sizeof(float));
        var destination = GetDestination(ref state, byteCount);
        if (destination.IsEmpty)
            throw new InvalidOperationException($"Column '{columnName}' requires {byteCount} bytes but MaxEncodedBytes is {maxEncodedBytes}.");

        if (BitConverter.IsLittleEndian)
            MemoryMarshal.AsBytes(values).CopyTo(destination);
        else
        {
            for (var i = 0; i < values.Length; i++)
                BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(i * 4, 4), BitConverter.SingleToInt32Bits(values[i]));
        }

        state.EncodedLength = byteCount;
        state.UncompressedLength = byteCount;
    }

    static void EncodePlainDouble(ReadOnlySpan<double> values, ref ParquetWriter.RowGroupState.ColumnState state, string columnName, int maxEncodedBytes)
    {
        var byteCount = checked(values.Length * sizeof(double));
        var destination = GetDestination(ref state, byteCount);
        if (destination.IsEmpty)
            throw new InvalidOperationException($"Column '{columnName}' requires {byteCount} bytes but MaxEncodedBytes is {maxEncodedBytes}.");

        if (BitConverter.IsLittleEndian)
            MemoryMarshal.AsBytes(values).CopyTo(destination);
        else
        {
            for (var i = 0; i < values.Length; i++)
                BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(i * 8, 8), BitConverter.DoubleToInt64Bits(values[i]));
        }

        state.EncodedLength = byteCount;
        state.UncompressedLength = byteCount;
    }

    static Span<byte> GetDestination(ref ParquetWriter.RowGroupState.ColumnState state, int byteCount)
    {
        if (state.EncodedBuffer is not null)
        {
            if (state.EncodedBuffer.Length < byteCount)
                return default;
            return state.EncodedBuffer.AsSpan(0, byteCount);
        }

        if (state.EncodedBufferOwner is null)
            return default;

        var memory = state.EncodedBufferOwner.Memory;
        if (memory.Length < byteCount)
            return default;

        return memory.Span[..byteCount];
    }

    static long ToUnixMicroseconds(DateTime value, DateTimeKindHandling handling, string columnName)
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
            _ => throw new NotSupportedException($"DateTime kind '{value.Kind}' is not supported for column '{columnName}'.")
        };
    }

    static DateTime NormalizeLocal(DateTime value, DateTimeKindHandling handling, string columnName)
    {
        if ((handling & DateTimeKindHandling.PreserveClockTime) != 0)
            return DateTime.SpecifyKind(value, DateTimeKind.Utc);
        if ((handling & DateTimeKindHandling.ConvertLocalToUtc) != 0)
            return value.ToUniversalTime();
        if ((handling & DateTimeKindHandling.RequireUtc) != 0 || handling == DateTimeKindHandling.None)
            throw new InvalidOperationException($"Column '{columnName}' received Local DateTime but policy requires UTC.");

        return value.ToUniversalTime();
    }

    static DateTime NormalizeUnspecified(DateTime value, DateTimeKindHandling handling, string columnName)
    {
        if ((handling & DateTimeKindHandling.PreserveClockTime) != 0)
            return DateTime.SpecifyKind(value, DateTimeKind.Utc);
        if ((handling & DateTimeKindHandling.AssumeUnspecifiedAsUtc) != 0)
            return DateTime.SpecifyKind(value, DateTimeKind.Utc);
        if ((handling & DateTimeKindHandling.RequireUtc) != 0 || handling == DateTimeKindHandling.None)
            throw new InvalidOperationException($"Column '{columnName}' received Unspecified DateTime but policy requires UTC.");

        throw new InvalidOperationException($"Column '{columnName}' received Unspecified DateTime and no policy was configured.");
    }

    static void ValidateDateTimeHandling(DateTimeKindHandling handling, string columnName)
    {
        if ((handling & DateTimeKindHandling.PreserveClockTime) == 0)
            return;
        if ((handling & (DateTimeKindHandling.ConvertLocalToUtc | DateTimeKindHandling.AssumeUnspecifiedAsUtc)) == 0)
            return;

        throw new InvalidOperationException($"Column '{columnName}' has conflicting DateTime handling flags. PreserveClockTime cannot be combined with ConvertLocalToUtc or AssumeUnspecifiedAsUtc.");
    }

    static bool TryEncodeOptional<T>(Column column, ReadOnlySpan<T> values, ColumnDispatch.DispatchKey dispatchKey, RowGroupOptions options, DateTimeKindHandling dateTimeKindHandling, ref ParquetWriter.RowGroupState.ColumnState state)
    {
        switch (dispatchKey)
        {
            case ColumnDispatch.DispatchKey.Int32Int32:
                EncodeOptionalInt32AllDefined(Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<int>>(ref values), ref state, column.Name, options.MaxEncodedBytes);
                return true;
            case ColumnDispatch.DispatchKey.Int32NullableInt32:
                EncodeOptionalInt32(Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<int?>>(ref values), ref state, column.Name, options.MaxEncodedBytes);
                return true;
            case ColumnDispatch.DispatchKey.ByteArrayString:
                EncodeOptionalString(Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<string>>(ref values), ref state, column.Name, options.MaxEncodedBytes);
                return true;
            case ColumnDispatch.DispatchKey.ByteArrayByteArray:
                EncodeOptionalByteArray(Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<byte[]>>(ref values), ref state, column.Name, options.MaxEncodedBytes);
                return true;
        }

        _ = dateTimeKindHandling;
        return false;
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

    static void EncodeOptionalInt32(ReadOnlySpan<int?> values, ref ParquetWriter.RowGroupState.ColumnState state, string columnName, int maxEncodedBytes)
    {
        var nonNullCount = 0;
        for (var i = 0; i < values.Length; i++)
        {
            if (values[i].HasValue)
                nonNullCount++;
        }

        var definitionByteCount = GetDefinitionLevelsByteCount(values.Length);
        var valuesByteCount = checked(nonNullCount * sizeof(int));
        var totalByteCount = checked(definitionByteCount + valuesByteCount);
        var destination = GetDestination(ref state, totalByteCount);
        if (totalByteCount > 0 && destination.IsEmpty)
            throw new InvalidOperationException($"Column '{columnName}' requires {totalByteCount} bytes but MaxEncodedBytes is {maxEncodedBytes}.");

        var nullCount = values.Length - nonNullCount;
        var writtenDefinitionBytes = WriteDefinitionLevels(values, destination);
        if (writtenDefinitionBytes != definitionByteCount)
            throw new InvalidOperationException($"Column '{columnName}' definition levels size mismatch.");

        var valueOffset = definitionByteCount;
        for (var i = 0; i < values.Length; i++)
        {
            var value = values[i];
            if (!value.HasValue)
                continue;

            BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(valueOffset, sizeof(int)), value.Value);
            valueOffset += sizeof(int);
        }

        SetOptionalLayout(ref state, totalByteCount, nullCount, definitionByteCount);
    }

    static void EncodeOptionalInt32AllDefined(ReadOnlySpan<int> values, ref ParquetWriter.RowGroupState.ColumnState state, string columnName, int maxEncodedBytes)
    {
        var definitionByteCount = GetDefinitionLevelsByteCount(values.Length);
        var valuesByteCount = checked(values.Length * sizeof(int));
        var totalByteCount = checked(definitionByteCount + valuesByteCount);
        var destination = GetDestination(ref state, totalByteCount);
        if (totalByteCount > 0 && destination.IsEmpty)
            throw new InvalidOperationException($"Column '{columnName}' requires {totalByteCount} bytes but MaxEncodedBytes is {maxEncodedBytes}.");

        WriteAllDefinedLevels(values.Length, destination);
        var valueDestination = destination[definitionByteCount..];
        if (BitConverter.IsLittleEndian)
            MemoryMarshal.AsBytes(values).CopyTo(valueDestination);
        else
        {
            for (var i = 0; i < values.Length; i++)
                BinaryPrimitives.WriteInt32LittleEndian(valueDestination.Slice(i * 4, 4), values[i]);
        }

        SetOptionalLayout(ref state, totalByteCount, 0, definitionByteCount);
    }

    static void EncodeOptionalString(ReadOnlySpan<string> values, ref ParquetWriter.RowGroupState.ColumnState state, string columnName, int maxEncodedBytes)
    {
        var nonNullCount = 0;
        var valuesByteCount = 0;
        for (var i = 0; i < values.Length; i++)
        {
            var value = values[i];
            if (value is null)
                continue;

            nonNullCount++;
            valuesByteCount = checked(valuesByteCount + sizeof(int));
            valuesByteCount = checked(valuesByteCount + Utf8.GetByteCount(value));
        }

        var definitionByteCount = GetDefinitionLevelsByteCount(values.Length);
        var totalByteCount = checked(definitionByteCount + valuesByteCount);
        var destination = GetDestination(ref state, totalByteCount);
        if (totalByteCount > 0 && destination.IsEmpty)
            throw new InvalidOperationException($"Column '{columnName}' requires {totalByteCount} bytes but MaxEncodedBytes is {maxEncodedBytes}.");

        var writtenDefinitionBytes = WriteDefinitionLevels(values, destination);
        if (writtenDefinitionBytes != definitionByteCount)
            throw new InvalidOperationException($"Column '{columnName}' definition levels size mismatch.");

        var valueOffset = definitionByteCount;
        for (var i = 0; i < values.Length; i++)
        {
            var value = values[i];
            if (value is null)
                continue;

            var utf8Length = Utf8.GetByteCount(value);
            BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(valueOffset, sizeof(int)), utf8Length);
            valueOffset += sizeof(int);
            if (utf8Length == 0)
                continue;

            var written = Utf8.GetBytes(value.AsSpan(), destination.Slice(valueOffset, utf8Length));
            if (written != utf8Length)
                throw new InvalidOperationException($"Column '{columnName}' could not encode UTF-8 payload.");
            valueOffset += utf8Length;
        }

        SetOptionalLayout(ref state, totalByteCount, values.Length - nonNullCount, definitionByteCount);
    }

    static void EncodeOptionalByteArray(ReadOnlySpan<byte[]> values, ref ParquetWriter.RowGroupState.ColumnState state, string columnName, int maxEncodedBytes)
    {
        var nonNullCount = 0;
        var valuesByteCount = 0;
        for (var i = 0; i < values.Length; i++)
        {
            var value = values[i];
            if (value is null)
                continue;

            nonNullCount++;
            valuesByteCount = checked(valuesByteCount + sizeof(int));
            valuesByteCount = checked(valuesByteCount + value.Length);
        }

        var definitionByteCount = GetDefinitionLevelsByteCount(values.Length);
        var totalByteCount = checked(definitionByteCount + valuesByteCount);
        var destination = GetDestination(ref state, totalByteCount);
        if (totalByteCount > 0 && destination.IsEmpty)
            throw new InvalidOperationException($"Column '{columnName}' requires {totalByteCount} bytes but MaxEncodedBytes is {maxEncodedBytes}.");

        var writtenDefinitionBytes = WriteDefinitionLevels(values, destination);
        if (writtenDefinitionBytes != definitionByteCount)
            throw new InvalidOperationException($"Column '{columnName}' definition levels size mismatch.");

        var valueOffset = definitionByteCount;
        for (var i = 0; i < values.Length; i++)
        {
            var value = values[i];
            if (value is null)
                continue;

            BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(valueOffset, sizeof(int)), value.Length);
            valueOffset += sizeof(int);
            if (value.Length == 0)
                continue;

            value.AsSpan().CopyTo(destination.Slice(valueOffset, value.Length));
            valueOffset += value.Length;
        }

        SetOptionalLayout(ref state, totalByteCount, values.Length - nonNullCount, definitionByteCount);
    }

    static int GetDefinitionLevelsByteCount(int valueCount)
    {
        if (valueCount == 0)
            return 0;

        var groupCount = (valueCount + 7) >> 3;
        var header = (uint)((groupCount << 1) | 1);
        return GetVarUInt32Length(header) + groupCount;
    }

    static int GetLevelByteCount(int valueCount, int bitWidth)
    {
        if (valueCount == 0)
            return 0;

        var groupCount = (valueCount + 7) >> 3;
        var header = (uint)((groupCount << 1) | 1);
        return checked(GetVarUInt32Length(header) + (groupCount * bitWidth));
    }

    static int WriteDefinitionLevels(ReadOnlySpan<int?> values, Span<byte> destination)
    {
        if (values.Length == 0)
            return 0;

        var groupCount = (values.Length + 7) >> 3;
        var header = (uint)((groupCount << 1) | 1);
        var offset = WriteVarUInt32(header, destination);
        for (var group = 0; group < groupCount; group++)
        {
            byte packed = 0;
            var groupStart = group << 3;
            for (var bit = 0; bit < 8; bit++)
            {
                var index = groupStart + bit;
                if (index >= values.Length)
                    break;
                if (values[index].HasValue)
                    packed |= (byte)(1 << bit);
            }

            destination[offset++] = packed;
        }

        return offset;
    }

    static int WriteDefinitionLevels(ReadOnlySpan<string> values, Span<byte> destination)
    {
        if (values.Length == 0)
            return 0;

        var groupCount = (values.Length + 7) >> 3;
        var header = (uint)((groupCount << 1) | 1);
        var offset = WriteVarUInt32(header, destination);
        for (var group = 0; group < groupCount; group++)
        {
            byte packed = 0;
            var groupStart = group << 3;
            for (var bit = 0; bit < 8; bit++)
            {
                var index = groupStart + bit;
                if (index >= values.Length)
                    break;
                if (values[index] is not null)
                    packed |= (byte)(1 << bit);
            }

            destination[offset++] = packed;
        }

        return offset;
    }

    static int WriteDefinitionLevels(ReadOnlySpan<byte[]> values, Span<byte> destination)
    {
        if (values.Length == 0)
            return 0;

        var groupCount = (values.Length + 7) >> 3;
        var header = (uint)((groupCount << 1) | 1);
        var offset = WriteVarUInt32(header, destination);
        for (var group = 0; group < groupCount; group++)
        {
            byte packed = 0;
            var groupStart = group << 3;
            for (var bit = 0; bit < 8; bit++)
            {
                var index = groupStart + bit;
                if (index >= values.Length)
                    break;
                if (values[index] is not null)
                    packed |= (byte)(1 << bit);
            }

            destination[offset++] = packed;
        }

        return offset;
    }

    static void WriteAllDefinedLevels(int valueCount, Span<byte> destination)
    {
        if (valueCount == 0)
            return;

        var groupCount = (valueCount + 7) >> 3;
        var header = (uint)((groupCount << 1) | 1);
        var offset = WriteVarUInt32(header, destination);
        for (var i = 0; i < groupCount; i++)
            destination[offset + i] = 0xFF;
    }

    static int WriteVarUInt32(uint value, Span<byte> destination)
    {
        var offset = 0;
        while (value >= 0x80)
        {
            destination[offset++] = (byte)(value | 0x80);
            value >>= 7;
        }

        destination[offset++] = (byte)value;
        return offset;
    }

    static int GetVarUInt32Length(uint value)
    {
        var length = 1;
        while (value >= 0x80)
        {
            value >>= 7;
            length++;
        }

        return length;
    }

    static int CountRepeatedValues<T>(ReadOnlySpan<T[]> rows, string columnName, out int emptyRowCount)
    {
        emptyRowCount = 0;
        var total = 0;
        for (var i = 0; i < rows.Length; i++)
        {
            var row = rows[i];
            if (row is null)
                throw new InvalidOperationException($"Column '{columnName}' does not support null row arrays.");
            if (row.Length == 0)
                emptyRowCount++;
            total = checked(total + row.Length);
        }

        return total;
    }

    static int WriteRepetitionLevels<T>(ReadOnlySpan<T[]> rows, int levelValueCount, Span<byte> destination)
    {
        if (levelValueCount == 0)
            return 0;

        var groupCount = (levelValueCount + 7) >> 3;
        var header = (uint)((groupCount << 1) | 1);
        var offset = WriteVarUInt32(header, destination);
        byte packed = 0;
        var bitIndex = 0;

        for (var rowIndex = 0; rowIndex < rows.Length; rowIndex++)
        {
            var row = rows[rowIndex];
            if (row.Length == 0)
            {
                bitIndex++;
                if (bitIndex != 8)
                    continue;

                destination[offset++] = packed;
                packed = 0;
                bitIndex = 0;
                continue;
            }

            for (var valueIndex = 0; valueIndex < row.Length; valueIndex++)
            {
                if (valueIndex > 0)
                    packed |= (byte)(1 << bitIndex);

                bitIndex++;
                if (bitIndex != 8)
                    continue;

                destination[offset++] = packed;
                packed = 0;
                bitIndex = 0;
            }
        }

        if (bitIndex > 0)
            destination[offset++] = packed;
        return offset;
    }

    static int WriteRepeatedDefinitionLevels<T>(ReadOnlySpan<T[]> rows, int levelValueCount, Span<byte> destination)
    {
        if (levelValueCount == 0)
            return 0;

        var groupCount = (levelValueCount + 7) >> 3;
        var header = (uint)((groupCount << 1) | 1);
        var offset = WriteVarUInt32(header, destination);
        byte packed = 0;
        var bitIndex = 0;

        for (var rowIndex = 0; rowIndex < rows.Length; rowIndex++)
        {
            var row = rows[rowIndex];
            if (row.Length == 0)
            {
                bitIndex++;
                if (bitIndex != 8)
                    continue;

                destination[offset++] = packed;
                packed = 0;
                bitIndex = 0;
                continue;
            }

            for (var valueIndex = 0; valueIndex < row.Length; valueIndex++)
            {
                packed |= (byte)(1 << bitIndex);
                bitIndex++;
                if (bitIndex != 8)
                    continue;

                destination[offset++] = packed;
                packed = 0;
                bitIndex = 0;
            }
        }

        if (bitIndex > 0)
            destination[offset++] = packed;
        return offset;
    }

    static int WriteRepeatedDefinitionLevelsOptionalInt32(ReadOnlySpan<int?[]> rows, int levelValueCount, Span<byte> destination)
    {
        if (levelValueCount == 0)
            return 0;

        var groupCount = (levelValueCount + 7) >> 3;
        var header = (uint)((groupCount << 1) | 1);
        var offset = WriteVarUInt32(header, destination);
        ushort packed = 0;
        var indexInGroup = 0;

        for (var rowIndex = 0; rowIndex < rows.Length; rowIndex++)
        {
            var row = rows[rowIndex];
            if (row.Length == 0)
            {
                indexInGroup++;
                if (indexInGroup != 8)
                    continue;

                destination[offset++] = (byte)packed;
                destination[offset++] = (byte)(packed >> 8);
                packed = 0;
                indexInGroup = 0;
                continue;
            }

            for (var elementIndex = 0; elementIndex < row.Length; elementIndex++)
            {
                var level = row[elementIndex].HasValue ? 2 : 1;
                packed |= (ushort)(level << (indexInGroup * 2));
                indexInGroup++;
                if (indexInGroup != 8)
                    continue;

                destination[offset++] = (byte)packed;
                destination[offset++] = (byte)(packed >> 8);
                packed = 0;
                indexInGroup = 0;
            }
        }

        if (indexInGroup > 0)
        {
            destination[offset++] = (byte)packed;
            destination[offset++] = (byte)(packed >> 8);
        }

        return offset;
    }

    static int WriteRepeatedDefinitionLevelsOptionalString(ReadOnlySpan<string[]> rows, int levelValueCount, Span<byte> destination)
    {
        if (levelValueCount == 0)
            return 0;

        var groupCount = (levelValueCount + 7) >> 3;
        var header = (uint)((groupCount << 1) | 1);
        var offset = WriteVarUInt32(header, destination);
        ushort packed = 0;
        var indexInGroup = 0;

        for (var rowIndex = 0; rowIndex < rows.Length; rowIndex++)
        {
            var row = rows[rowIndex];
            if (row.Length == 0)
            {
                indexInGroup++;
                if (indexInGroup != 8)
                    continue;

                destination[offset++] = (byte)packed;
                destination[offset++] = (byte)(packed >> 8);
                packed = 0;
                indexInGroup = 0;
                continue;
            }

            for (var elementIndex = 0; elementIndex < row.Length; elementIndex++)
            {
                var level = row[elementIndex] is null ? 1 : 2;
                packed |= (ushort)(level << (indexInGroup * 2));
                indexInGroup++;
                if (indexInGroup != 8)
                    continue;

                destination[offset++] = (byte)packed;
                destination[offset++] = (byte)(packed >> 8);
                packed = 0;
                indexInGroup = 0;
            }
        }

        if (indexInGroup > 0)
        {
            destination[offset++] = (byte)packed;
            destination[offset++] = (byte)(packed >> 8);
        }

        return offset;
    }

    static int WriteRepeatedDefinitionLevelsOptionalByteArray(ReadOnlySpan<byte[][]> rows, int levelValueCount, Span<byte> destination)
    {
        if (levelValueCount == 0)
            return 0;

        var groupCount = (levelValueCount + 7) >> 3;
        var header = (uint)((groupCount << 1) | 1);
        var offset = WriteVarUInt32(header, destination);
        ushort packed = 0;
        var indexInGroup = 0;

        for (var rowIndex = 0; rowIndex < rows.Length; rowIndex++)
        {
            var row = rows[rowIndex];
            if (row.Length == 0)
            {
                indexInGroup++;
                if (indexInGroup != 8)
                    continue;

                destination[offset++] = (byte)packed;
                destination[offset++] = (byte)(packed >> 8);
                packed = 0;
                indexInGroup = 0;
                continue;
            }

            for (var elementIndex = 0; elementIndex < row.Length; elementIndex++)
            {
                var level = row[elementIndex] is null ? 1 : 2;
                packed |= (ushort)(level << (indexInGroup * 2));
                indexInGroup++;
                if (indexInGroup != 8)
                    continue;

                destination[offset++] = (byte)packed;
                destination[offset++] = (byte)(packed >> 8);
                packed = 0;
                indexInGroup = 0;
            }
        }

        if (indexInGroup > 0)
        {
            destination[offset++] = (byte)packed;
            destination[offset++] = (byte)(packed >> 8);
        }

        return offset;
    }

    static void SetRepeatedLayout(ref ParquetWriter.RowGroupState.ColumnState state, int rowCount, int levelValueCount, int nullCount, int totalByteCount, int repetitionByteCount, int definitionByteCount)
    {
        state.RowCount = rowCount;
        state.ValueCount = levelValueCount;
        state.EncodedLength = totalByteCount;
        state.UncompressedLength = totalByteCount;
        state.NullCount = nullCount;
        state.RepetitionLevelsByteLength = repetitionByteCount;
        state.DefinitionLevelsByteLength = definitionByteCount;
    }

    static void EncodeRepeatedInt32(ReadOnlySpan<int[]> rows, ref ParquetWriter.RowGroupState.ColumnState state, string columnName, int maxEncodedBytes)
    {
        var dataValueCount = CountRepeatedValues(rows, columnName, out var emptyRowCount);
        var levelValueCount = checked(dataValueCount + emptyRowCount);
        var repetitionByteCount = GetDefinitionLevelsByteCount(levelValueCount);
        var definitionByteCount = GetDefinitionLevelsByteCount(levelValueCount);
        var valuesByteCount = checked(dataValueCount * sizeof(int));
        var totalByteCount = checked(repetitionByteCount + definitionByteCount + valuesByteCount);
        var destination = GetDestination(ref state, totalByteCount);
        if (totalByteCount > 0 && destination.IsEmpty)
            throw new InvalidOperationException($"Column '{columnName}' requires {totalByteCount} bytes but MaxEncodedBytes is {maxEncodedBytes}.");

        var repetitionWritten = WriteRepetitionLevels(rows, levelValueCount, destination);
        if (repetitionWritten != repetitionByteCount)
            throw new InvalidOperationException($"Column '{columnName}' repetition levels size mismatch.");
        var definitionWritten = WriteRepeatedDefinitionLevels(rows, levelValueCount, destination[repetitionByteCount..]);
        if (definitionWritten != definitionByteCount)
            throw new InvalidOperationException($"Column '{columnName}' definition levels size mismatch.");

        var offset = repetitionByteCount + definitionByteCount;
        for (var rowIndex = 0; rowIndex < rows.Length; rowIndex++)
        {
            var row = rows[rowIndex];
            for (var valueIndex = 0; valueIndex < row.Length; valueIndex++)
            {
                BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(offset, sizeof(int)), row[valueIndex]);
                offset += sizeof(int);
            }
        }

        SetRepeatedLayout(ref state, rows.Length, levelValueCount, emptyRowCount, totalByteCount, repetitionByteCount, definitionByteCount);
    }

    static void EncodeRepeatedDateOnly(ReadOnlySpan<DateOnly[]> rows, ref ParquetWriter.RowGroupState.ColumnState state, string columnName, int maxEncodedBytes)
    {
        var dataValueCount = CountRepeatedValues(rows, columnName, out var emptyRowCount);
        var levelValueCount = checked(dataValueCount + emptyRowCount);
        var repetitionByteCount = GetDefinitionLevelsByteCount(levelValueCount);
        var definitionByteCount = GetDefinitionLevelsByteCount(levelValueCount);
        var valuesByteCount = checked(dataValueCount * sizeof(int));
        var totalByteCount = checked(repetitionByteCount + definitionByteCount + valuesByteCount);
        var destination = GetDestination(ref state, totalByteCount);
        if (totalByteCount > 0 && destination.IsEmpty)
            throw new InvalidOperationException($"Column '{columnName}' requires {totalByteCount} bytes but MaxEncodedBytes is {maxEncodedBytes}.");

        var repetitionWritten = WriteRepetitionLevels(rows, levelValueCount, destination);
        if (repetitionWritten != repetitionByteCount)
            throw new InvalidOperationException($"Column '{columnName}' repetition levels size mismatch.");
        var definitionWritten = WriteRepeatedDefinitionLevels(rows, levelValueCount, destination[repetitionByteCount..]);
        if (definitionWritten != definitionByteCount)
            throw new InvalidOperationException($"Column '{columnName}' definition levels size mismatch.");

        var offset = repetitionByteCount + definitionByteCount;
        for (var rowIndex = 0; rowIndex < rows.Length; rowIndex++)
        {
            var row = rows[rowIndex];
            for (var valueIndex = 0; valueIndex < row.Length; valueIndex++)
            {
                var daysSinceEpoch = checked(row[valueIndex].DayNumber - UnixEpochDayNumber);
                BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(offset, sizeof(int)), daysSinceEpoch);
                offset += sizeof(int);
            }
        }

        SetRepeatedLayout(ref state, rows.Length, levelValueCount, emptyRowCount, totalByteCount, repetitionByteCount, definitionByteCount);
    }

    static void EncodeRepeatedNullableInt32(ReadOnlySpan<int?[]> rows, ref ParquetWriter.RowGroupState.ColumnState state, string columnName, int maxEncodedBytes)
    {
        var elementCount = 0;
        var emptyRowCount = 0;
        var nonNullCount = 0;
        for (var rowIndex = 0; rowIndex < rows.Length; rowIndex++)
        {
            var row = rows[rowIndex] ?? throw new InvalidOperationException($"Column '{columnName}' does not support null row arrays.");
            if (row.Length == 0)
                emptyRowCount++;
            elementCount = checked(elementCount + row.Length);
            for (var elementIndex = 0; elementIndex < row.Length; elementIndex++)
            {
                if (row[elementIndex].HasValue)
                    nonNullCount++;
            }
        }

        var levelValueCount = checked(elementCount + emptyRowCount);
        var repetitionByteCount = GetDefinitionLevelsByteCount(levelValueCount);
        var definitionByteCount = GetLevelByteCount(levelValueCount, 2);
        var valuesByteCount = checked(nonNullCount * sizeof(int));
        var totalByteCount = checked(repetitionByteCount + definitionByteCount + valuesByteCount);
        var destination = GetDestination(ref state, totalByteCount);
        if (totalByteCount > 0 && destination.IsEmpty)
            throw new InvalidOperationException($"Column '{columnName}' requires {totalByteCount} bytes but MaxEncodedBytes is {maxEncodedBytes}.");

        var repetitionWritten = WriteRepetitionLevels(rows, levelValueCount, destination);
        if (repetitionWritten != repetitionByteCount)
            throw new InvalidOperationException($"Column '{columnName}' repetition levels size mismatch.");
        var definitionWritten = WriteRepeatedDefinitionLevelsOptionalInt32(rows, levelValueCount, destination[repetitionByteCount..]);
        if (definitionWritten != definitionByteCount)
            throw new InvalidOperationException($"Column '{columnName}' definition levels size mismatch.");

        var offset = repetitionByteCount + definitionByteCount;
        for (var rowIndex = 0; rowIndex < rows.Length; rowIndex++)
        {
            var row = rows[rowIndex];
            for (var elementIndex = 0; elementIndex < row.Length; elementIndex++)
            {
                var value = row[elementIndex];
                if (!value.HasValue)
                    continue;

                BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(offset, sizeof(int)), value.Value);
                offset += sizeof(int);
            }
        }

        var nullCount = checked(levelValueCount - nonNullCount);
        SetRepeatedLayout(ref state, rows.Length, levelValueCount, nullCount, totalByteCount, repetitionByteCount, definitionByteCount);
    }

    static void EncodeRepeatedInt64(ReadOnlySpan<long[]> rows, ref ParquetWriter.RowGroupState.ColumnState state, string columnName, int maxEncodedBytes)
    {
        var dataValueCount = CountRepeatedValues(rows, columnName, out var emptyRowCount);
        var levelValueCount = checked(dataValueCount + emptyRowCount);
        var repetitionByteCount = GetDefinitionLevelsByteCount(levelValueCount);
        var definitionByteCount = GetDefinitionLevelsByteCount(levelValueCount);
        var valuesByteCount = checked(dataValueCount * sizeof(long));
        var totalByteCount = checked(repetitionByteCount + definitionByteCount + valuesByteCount);
        var destination = GetDestination(ref state, totalByteCount);
        if (totalByteCount > 0 && destination.IsEmpty)
            throw new InvalidOperationException($"Column '{columnName}' requires {totalByteCount} bytes but MaxEncodedBytes is {maxEncodedBytes}.");

        var repetitionWritten = WriteRepetitionLevels(rows, levelValueCount, destination);
        if (repetitionWritten != repetitionByteCount)
            throw new InvalidOperationException($"Column '{columnName}' repetition levels size mismatch.");
        var definitionWritten = WriteRepeatedDefinitionLevels(rows, levelValueCount, destination[repetitionByteCount..]);
        if (definitionWritten != definitionByteCount)
            throw new InvalidOperationException($"Column '{columnName}' definition levels size mismatch.");

        var offset = repetitionByteCount + definitionByteCount;
        for (var rowIndex = 0; rowIndex < rows.Length; rowIndex++)
        {
            var row = rows[rowIndex];
            for (var valueIndex = 0; valueIndex < row.Length; valueIndex++)
            {
                BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(offset, sizeof(long)), row[valueIndex]);
                offset += sizeof(long);
            }
        }

        SetRepeatedLayout(ref state, rows.Length, levelValueCount, emptyRowCount, totalByteCount, repetitionByteCount, definitionByteCount);
    }

    static void EncodeRepeatedDateTime(ReadOnlySpan<DateTime[]> rows, DateTimeKindHandling dateTimeKindHandling, ref ParquetWriter.RowGroupState.ColumnState state, string columnName, int maxEncodedBytes)
    {
        var dataValueCount = CountRepeatedValues(rows, columnName, out var emptyRowCount);
        var levelValueCount = checked(dataValueCount + emptyRowCount);
        var repetitionByteCount = GetDefinitionLevelsByteCount(levelValueCount);
        var definitionByteCount = GetDefinitionLevelsByteCount(levelValueCount);
        var valuesByteCount = checked(dataValueCount * sizeof(long));
        var totalByteCount = checked(repetitionByteCount + definitionByteCount + valuesByteCount);
        var destination = GetDestination(ref state, totalByteCount);
        if (totalByteCount > 0 && destination.IsEmpty)
            throw new InvalidOperationException($"Column '{columnName}' requires {totalByteCount} bytes but MaxEncodedBytes is {maxEncodedBytes}.");

        ValidateDateTimeHandling(dateTimeKindHandling, columnName);
        var repetitionWritten = WriteRepetitionLevels(rows, levelValueCount, destination);
        if (repetitionWritten != repetitionByteCount)
            throw new InvalidOperationException($"Column '{columnName}' repetition levels size mismatch.");
        var definitionWritten = WriteRepeatedDefinitionLevels(rows, levelValueCount, destination[repetitionByteCount..]);
        if (definitionWritten != definitionByteCount)
            throw new InvalidOperationException($"Column '{columnName}' definition levels size mismatch.");

        var offset = repetitionByteCount + definitionByteCount;
        for (var rowIndex = 0; rowIndex < rows.Length; rowIndex++)
        {
            var row = rows[rowIndex];
            for (var valueIndex = 0; valueIndex < row.Length; valueIndex++)
            {
                var unixMicros = ToUnixMicroseconds(row[valueIndex], dateTimeKindHandling, columnName);
                BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(offset, sizeof(long)), unixMicros);
                offset += sizeof(long);
            }
        }

        SetRepeatedLayout(ref state, rows.Length, levelValueCount, emptyRowCount, totalByteCount, repetitionByteCount, definitionByteCount);
    }

    static void EncodeRepeatedDateTimeOffset(ReadOnlySpan<DateTimeOffset[]> rows, ref ParquetWriter.RowGroupState.ColumnState state, string columnName, int maxEncodedBytes)
    {
        var dataValueCount = CountRepeatedValues(rows, columnName, out var emptyRowCount);
        var levelValueCount = checked(dataValueCount + emptyRowCount);
        var repetitionByteCount = GetDefinitionLevelsByteCount(levelValueCount);
        var definitionByteCount = GetDefinitionLevelsByteCount(levelValueCount);
        var valuesByteCount = checked(dataValueCount * sizeof(long));
        var totalByteCount = checked(repetitionByteCount + definitionByteCount + valuesByteCount);
        var destination = GetDestination(ref state, totalByteCount);
        if (totalByteCount > 0 && destination.IsEmpty)
            throw new InvalidOperationException($"Column '{columnName}' requires {totalByteCount} bytes but MaxEncodedBytes is {maxEncodedBytes}.");

        var repetitionWritten = WriteRepetitionLevels(rows, levelValueCount, destination);
        if (repetitionWritten != repetitionByteCount)
            throw new InvalidOperationException($"Column '{columnName}' repetition levels size mismatch.");
        var definitionWritten = WriteRepeatedDefinitionLevels(rows, levelValueCount, destination[repetitionByteCount..]);
        if (definitionWritten != definitionByteCount)
            throw new InvalidOperationException($"Column '{columnName}' definition levels size mismatch.");

        var offset = repetitionByteCount + definitionByteCount;
        for (var rowIndex = 0; rowIndex < rows.Length; rowIndex++)
        {
            var row = rows[rowIndex];
            for (var valueIndex = 0; valueIndex < row.Length; valueIndex++)
            {
                var deltaTicks = checked(row[valueIndex].UtcTicks - UnixEpochTicks);
                var unixMicros = deltaTicks / TicksPerMicrosecond;
                BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(offset, sizeof(long)), unixMicros);
                offset += sizeof(long);
            }
        }

        SetRepeatedLayout(ref state, rows.Length, levelValueCount, emptyRowCount, totalByteCount, repetitionByteCount, definitionByteCount);
    }

    static void EncodeRepeatedTimeOnly(ReadOnlySpan<TimeOnly[]> rows, ref ParquetWriter.RowGroupState.ColumnState state, string columnName, int maxEncodedBytes)
    {
        var dataValueCount = CountRepeatedValues(rows, columnName, out var emptyRowCount);
        var levelValueCount = checked(dataValueCount + emptyRowCount);
        var repetitionByteCount = GetDefinitionLevelsByteCount(levelValueCount);
        var definitionByteCount = GetDefinitionLevelsByteCount(levelValueCount);
        var valuesByteCount = checked(dataValueCount * sizeof(long));
        var totalByteCount = checked(repetitionByteCount + definitionByteCount + valuesByteCount);
        var destination = GetDestination(ref state, totalByteCount);
        if (totalByteCount > 0 && destination.IsEmpty)
            throw new InvalidOperationException($"Column '{columnName}' requires {totalByteCount} bytes but MaxEncodedBytes is {maxEncodedBytes}.");

        var repetitionWritten = WriteRepetitionLevels(rows, levelValueCount, destination);
        if (repetitionWritten != repetitionByteCount)
            throw new InvalidOperationException($"Column '{columnName}' repetition levels size mismatch.");
        var definitionWritten = WriteRepeatedDefinitionLevels(rows, levelValueCount, destination[repetitionByteCount..]);
        if (definitionWritten != definitionByteCount)
            throw new InvalidOperationException($"Column '{columnName}' definition levels size mismatch.");

        var offset = repetitionByteCount + definitionByteCount;
        for (var rowIndex = 0; rowIndex < rows.Length; rowIndex++)
        {
            var row = rows[rowIndex];
            for (var valueIndex = 0; valueIndex < row.Length; valueIndex++)
            {
                var micros = row[valueIndex].Ticks / TicksPerMicrosecond;
                BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(offset, sizeof(long)), micros);
                offset += sizeof(long);
            }
        }

        SetRepeatedLayout(ref state, rows.Length, levelValueCount, emptyRowCount, totalByteCount, repetitionByteCount, definitionByteCount);
    }

    static void EncodeRepeatedFloat(ReadOnlySpan<float[]> rows, ref ParquetWriter.RowGroupState.ColumnState state, string columnName, int maxEncodedBytes)
    {
        var dataValueCount = CountRepeatedValues(rows, columnName, out var emptyRowCount);
        var levelValueCount = checked(dataValueCount + emptyRowCount);
        var repetitionByteCount = GetDefinitionLevelsByteCount(levelValueCount);
        var definitionByteCount = GetDefinitionLevelsByteCount(levelValueCount);
        var valuesByteCount = checked(dataValueCount * sizeof(float));
        var totalByteCount = checked(repetitionByteCount + definitionByteCount + valuesByteCount);
        var destination = GetDestination(ref state, totalByteCount);
        if (totalByteCount > 0 && destination.IsEmpty)
            throw new InvalidOperationException($"Column '{columnName}' requires {totalByteCount} bytes but MaxEncodedBytes is {maxEncodedBytes}.");

        var repetitionWritten = WriteRepetitionLevels(rows, levelValueCount, destination);
        if (repetitionWritten != repetitionByteCount)
            throw new InvalidOperationException($"Column '{columnName}' repetition levels size mismatch.");
        var definitionWritten = WriteRepeatedDefinitionLevels(rows, levelValueCount, destination[repetitionByteCount..]);
        if (definitionWritten != definitionByteCount)
            throw new InvalidOperationException($"Column '{columnName}' definition levels size mismatch.");

        var offset = repetitionByteCount + definitionByteCount;
        for (var rowIndex = 0; rowIndex < rows.Length; rowIndex++)
        {
            var row = rows[rowIndex];
            for (var valueIndex = 0; valueIndex < row.Length; valueIndex++)
            {
                BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(offset, sizeof(float)), BitConverter.SingleToInt32Bits(row[valueIndex]));
                offset += sizeof(float);
            }
        }

        SetRepeatedLayout(ref state, rows.Length, levelValueCount, emptyRowCount, totalByteCount, repetitionByteCount, definitionByteCount);
    }

    static void EncodeRepeatedDouble(ReadOnlySpan<double[]> rows, ref ParquetWriter.RowGroupState.ColumnState state, string columnName, int maxEncodedBytes)
    {
        var dataValueCount = CountRepeatedValues(rows, columnName, out var emptyRowCount);
        var levelValueCount = checked(dataValueCount + emptyRowCount);
        var repetitionByteCount = GetDefinitionLevelsByteCount(levelValueCount);
        var definitionByteCount = GetDefinitionLevelsByteCount(levelValueCount);
        var valuesByteCount = checked(dataValueCount * sizeof(double));
        var totalByteCount = checked(repetitionByteCount + definitionByteCount + valuesByteCount);
        var destination = GetDestination(ref state, totalByteCount);
        if (totalByteCount > 0 && destination.IsEmpty)
            throw new InvalidOperationException($"Column '{columnName}' requires {totalByteCount} bytes but MaxEncodedBytes is {maxEncodedBytes}.");

        var repetitionWritten = WriteRepetitionLevels(rows, levelValueCount, destination);
        if (repetitionWritten != repetitionByteCount)
            throw new InvalidOperationException($"Column '{columnName}' repetition levels size mismatch.");
        var definitionWritten = WriteRepeatedDefinitionLevels(rows, levelValueCount, destination[repetitionByteCount..]);
        if (definitionWritten != definitionByteCount)
            throw new InvalidOperationException($"Column '{columnName}' definition levels size mismatch.");

        var offset = repetitionByteCount + definitionByteCount;
        for (var rowIndex = 0; rowIndex < rows.Length; rowIndex++)
        {
            var row = rows[rowIndex];
            for (var valueIndex = 0; valueIndex < row.Length; valueIndex++)
            {
                BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(offset, sizeof(double)), BitConverter.DoubleToInt64Bits(row[valueIndex]));
                offset += sizeof(double);
            }
        }

        SetRepeatedLayout(ref state, rows.Length, levelValueCount, emptyRowCount, totalByteCount, repetitionByteCount, definitionByteCount);
    }

    static void EncodeRepeatedBoolean(ReadOnlySpan<bool[]> rows, ref ParquetWriter.RowGroupState.ColumnState state, string columnName, int maxEncodedBytes)
    {
        var dataValueCount = CountRepeatedValues(rows, columnName, out var emptyRowCount);
        var levelValueCount = checked(dataValueCount + emptyRowCount);
        var repetitionByteCount = GetDefinitionLevelsByteCount(levelValueCount);
        var definitionByteCount = GetDefinitionLevelsByteCount(levelValueCount);
        var valuesByteCount = (dataValueCount + 7) >> 3;
        var totalByteCount = checked(repetitionByteCount + definitionByteCount + valuesByteCount);
        var destination = GetDestination(ref state, totalByteCount);
        if (totalByteCount > 0 && destination.IsEmpty)
            throw new InvalidOperationException($"Column '{columnName}' requires {totalByteCount} bytes but MaxEncodedBytes is {maxEncodedBytes}.");

        var repetitionWritten = WriteRepetitionLevels(rows, levelValueCount, destination);
        if (repetitionWritten != repetitionByteCount)
            throw new InvalidOperationException($"Column '{columnName}' repetition levels size mismatch.");
        var definitionWritten = WriteRepeatedDefinitionLevels(rows, levelValueCount, destination[repetitionByteCount..]);
        if (definitionWritten != definitionByteCount)
            throw new InvalidOperationException($"Column '{columnName}' definition levels size mismatch.");

        var valueDestination = destination[(repetitionByteCount + definitionByteCount)..];
        valueDestination.Clear();
        var flattenedIndex = 0;
        for (var rowIndex = 0; rowIndex < rows.Length; rowIndex++)
        {
            var row = rows[rowIndex];
            for (var valueIndex = 0; valueIndex < row.Length; valueIndex++)
            {
                if (row[valueIndex])
                {
                    var byteIndex = flattenedIndex >> 3;
                    var bitIndex = flattenedIndex & 7;
                    valueDestination[byteIndex] |= (byte)(1 << bitIndex);
                }

                flattenedIndex++;
            }
        }

        SetRepeatedLayout(ref state, rows.Length, levelValueCount, emptyRowCount, totalByteCount, repetitionByteCount, definitionByteCount);
    }

    static void EncodeRepeatedString(ReadOnlySpan<string[]> rows, ref ParquetWriter.RowGroupState.ColumnState state, string columnName, int maxEncodedBytes)
    {
        var elementCount = 0;
        var emptyRowCount = 0;
        var nonNullCount = 0;
        var valuesByteCount = 0;
        for (var rowIndex = 0; rowIndex < rows.Length; rowIndex++)
        {
            var row = rows[rowIndex] ?? throw new InvalidOperationException($"Column '{columnName}' does not support null row arrays.");
            if (row.Length == 0)
                emptyRowCount++;
            elementCount = checked(elementCount + row.Length);
            for (var valueIndex = 0; valueIndex < row.Length; valueIndex++)
            {
                var value = row[valueIndex];
                if (value is null)
                    continue;

                nonNullCount++;
                valuesByteCount = checked(valuesByteCount + sizeof(int));
                valuesByteCount = checked(valuesByteCount + Utf8.GetByteCount(value));
            }
        }

        var levelValueCount = checked(elementCount + emptyRowCount);
        var repetitionByteCount = GetDefinitionLevelsByteCount(levelValueCount);
        var definitionByteCount = GetLevelByteCount(levelValueCount, 2);
        var totalByteCount = checked(repetitionByteCount + definitionByteCount + valuesByteCount);
        var destination = GetDestination(ref state, totalByteCount);
        if (totalByteCount > 0 && destination.IsEmpty)
            throw new InvalidOperationException($"Column '{columnName}' requires {totalByteCount} bytes but MaxEncodedBytes is {maxEncodedBytes}.");

        var repetitionWritten = WriteRepetitionLevels(rows, levelValueCount, destination);
        if (repetitionWritten != repetitionByteCount)
            throw new InvalidOperationException($"Column '{columnName}' repetition levels size mismatch.");
        var definitionWritten = WriteRepeatedDefinitionLevelsOptionalString(rows, levelValueCount, destination[repetitionByteCount..]);
        if (definitionWritten != definitionByteCount)
            throw new InvalidOperationException($"Column '{columnName}' definition levels size mismatch.");

        var offset = repetitionByteCount + definitionByteCount;
        for (var rowIndex = 0; rowIndex < rows.Length; rowIndex++)
        {
            var row = rows[rowIndex];
            for (var valueIndex = 0; valueIndex < row.Length; valueIndex++)
            {
                var value = row[valueIndex];
                if (value is null)
                    continue;

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
        }

        var nullCount = checked(levelValueCount - nonNullCount);
        SetRepeatedLayout(ref state, rows.Length, levelValueCount, nullCount, totalByteCount, repetitionByteCount, definitionByteCount);
    }

    static void EncodeRepeatedByteArray(ReadOnlySpan<byte[][]> rows, ref ParquetWriter.RowGroupState.ColumnState state, string columnName, int maxEncodedBytes)
    {
        var elementCount = 0;
        var emptyRowCount = 0;
        var nonNullCount = 0;
        var valuesByteCount = 0;
        for (var rowIndex = 0; rowIndex < rows.Length; rowIndex++)
        {
            var row = rows[rowIndex] ?? throw new InvalidOperationException($"Column '{columnName}' does not support null row arrays.");
            if (row.Length == 0)
                emptyRowCount++;
            elementCount = checked(elementCount + row.Length);
            for (var valueIndex = 0; valueIndex < row.Length; valueIndex++)
            {
                var value = row[valueIndex];
                if (value is null)
                    continue;

                nonNullCount++;
                valuesByteCount = checked(valuesByteCount + sizeof(int));
                valuesByteCount = checked(valuesByteCount + value.Length);
            }
        }

        var levelValueCount = checked(elementCount + emptyRowCount);
        var repetitionByteCount = GetDefinitionLevelsByteCount(levelValueCount);
        var definitionByteCount = GetLevelByteCount(levelValueCount, 2);
        var totalByteCount = checked(repetitionByteCount + definitionByteCount + valuesByteCount);
        var destination = GetDestination(ref state, totalByteCount);
        if (totalByteCount > 0 && destination.IsEmpty)
            throw new InvalidOperationException($"Column '{columnName}' requires {totalByteCount} bytes but MaxEncodedBytes is {maxEncodedBytes}.");

        var repetitionWritten = WriteRepetitionLevels(rows, levelValueCount, destination);
        if (repetitionWritten != repetitionByteCount)
            throw new InvalidOperationException($"Column '{columnName}' repetition levels size mismatch.");
        var definitionWritten = WriteRepeatedDefinitionLevelsOptionalByteArray(rows, levelValueCount, destination[repetitionByteCount..]);
        if (definitionWritten != definitionByteCount)
            throw new InvalidOperationException($"Column '{columnName}' definition levels size mismatch.");

        var offset = repetitionByteCount + definitionByteCount;
        for (var rowIndex = 0; rowIndex < rows.Length; rowIndex++)
        {
            var row = rows[rowIndex];
            for (var valueIndex = 0; valueIndex < row.Length; valueIndex++)
            {
                var value = row[valueIndex];
                if (value is null)
                    continue;

                BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(offset, sizeof(int)), value.Length);
                offset += sizeof(int);
                if (value.Length == 0)
                    continue;

                value.AsSpan().CopyTo(destination[offset..]);
                offset += value.Length;
            }
        }

        var nullCount = checked(levelValueCount - nonNullCount);
        SetRepeatedLayout(ref state, rows.Length, levelValueCount, nullCount, totalByteCount, repetitionByteCount, definitionByteCount);
    }

    static void SetOptionalLayout(ref ParquetWriter.RowGroupState.ColumnState state, int totalByteCount, int nullCount, int definitionByteCount)
    {
        state.EncodedLength = totalByteCount;
        state.UncompressedLength = totalByteCount;
        state.NullCount = nullCount;
        state.DefinitionLevelsByteLength = definitionByteCount;
        state.RepetitionLevelsByteLength = 0;
    }
}
