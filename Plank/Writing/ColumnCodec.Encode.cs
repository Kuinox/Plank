using Plank.Schema;
using System.Runtime.CompilerServices;
using System.Buffers;

namespace Plank.Writing;

static partial class ColumnCodec
{
    internal static void Encode<T>(Column column, ReadOnlySpan<T> values, ParquetPhysicalType physicalType,
        DateTimeKindHandling dateTimeKindHandling, ref ParquetWriter.RowGroupState.ColumnState state)
    {
        var encoding = ResolveDefaultEncoding(column.Options.Encodings);
        var encodedBufferCapacity = GetDestinationCapacity(ref state);
        state.Encoding = encoding;
        var valueKind = ColumnDispatch.GetValueKind<T>();
        var dispatchKey = ColumnDispatch.GetDispatchKey(physicalType, valueKind);
        if (column.Options.Repetition is ParquetRepetition.Optional)
        {
            if (encoding != EncodingKind.Plain)
                throw new NotSupportedException($"Encoding '{encoding}' is not supported for optional column '{column.Name}'.");
            if (TryEncodeOptional(column, values, dispatchKey, encodedBufferCapacity, dateTimeKindHandling, ref state))
                return;

            throw new NotSupportedException(
                $"Optional column '{column.Name}' is not supported for value type '{typeof(T)}' yet.");
        }

        switch (encoding)
        {
            case EncodingKind.Plain:
                EncodePlainRequired(values, dispatchKey, dateTimeKindHandling, ref state, column.Name, encodedBufferCapacity, physicalType);
                break;
            case EncodingKind.DeltaBinaryPacked:
                EncodeDeltaBinaryPackedRequired(values, dispatchKey, dateTimeKindHandling, ref state, column.Name, encodedBufferCapacity, physicalType);
                break;
            case EncodingKind.DeltaLengthByteArray:
                EncodeDeltaLengthByteArrayRequired(values, dispatchKey, ref state, column.Name, encodedBufferCapacity, physicalType);
                break;
            case EncodingKind.DeltaByteArray:
                EncodeDeltaByteArrayRequired(values, dispatchKey, ref state, column.Name, encodedBufferCapacity, physicalType);
                break;
            case EncodingKind.ByteStreamSplit:
                EncodeByteStreamSplitRequired(values, dispatchKey, dateTimeKindHandling, ref state, column.Name, encodedBufferCapacity, physicalType);
                break;
            default:
                throw new NotSupportedException($"Encoding '{encoding}' is not supported for column '{column.Name}'.");
        }

        state.NullCount = 0;
        state.DefinitionLevelsByteLength = 0;
        state.RepetitionLevelsByteLength = 0;
    }

    internal static void EncodeRepeated<T>(Column column, ReadOnlySpan<T[]> rows, ParquetPhysicalType physicalType,
        DateTimeKindHandling dateTimeKindHandling, ref ParquetWriter.RowGroupState.ColumnState state)
    {
        if (column.Options.Repetition is not ParquetRepetition.Repeated)
            throw new InvalidOperationException($"Column '{column.Name}' is not configured as Repeated.");

        var encoding = ResolveDefaultEncoding(column.Options.Encodings);
        if (encoding != EncodingKind.Plain)
            throw new NotSupportedException($"Encoding '{encoding}' is not supported for column '{column.Name}'.");

        var encodedBufferCapacity = GetDestinationCapacity(ref state);
        state.Encoding = encoding;
        var valueKind = ColumnDispatch.GetValueKind<T>();
        var dispatchKey = ColumnDispatch.GetDispatchKey(physicalType, valueKind);
        switch (dispatchKey)
        {
            case ColumnDispatch.DispatchKey.BooleanBool:
                EncodeRepeatedBoolean(Unsafe.As<ReadOnlySpan<T[]>, ReadOnlySpan<bool[]>>(ref rows), ref state,
                    column.Name, encodedBufferCapacity);
                break;
            case ColumnDispatch.DispatchKey.Int32Int32:
                EncodeRepeatedInt32(Unsafe.As<ReadOnlySpan<T[]>, ReadOnlySpan<int[]>>(ref rows), ref state, column.Name,
                    encodedBufferCapacity);
                break;
            case ColumnDispatch.DispatchKey.Int32NullableInt32:
                EncodeRepeatedNullableInt32(Unsafe.As<ReadOnlySpan<T[]>, ReadOnlySpan<int?[]>>(ref rows), ref state,
                    column.Name, encodedBufferCapacity);
                break;
            case ColumnDispatch.DispatchKey.Int32DateOnly:
                EncodeRepeatedDateOnly(Unsafe.As<ReadOnlySpan<T[]>, ReadOnlySpan<DateOnly[]>>(ref rows), ref state,
                    column.Name, encodedBufferCapacity);
                break;
            case ColumnDispatch.DispatchKey.Int64Int64:
                EncodeRepeatedInt64(Unsafe.As<ReadOnlySpan<T[]>, ReadOnlySpan<long[]>>(ref rows), ref state,
                    column.Name, encodedBufferCapacity);
                break;
            case ColumnDispatch.DispatchKey.Int64DateTime:
                EncodeRepeatedDateTime(Unsafe.As<ReadOnlySpan<T[]>, ReadOnlySpan<DateTime[]>>(ref rows),
                    dateTimeKindHandling, ref state, column.Name, encodedBufferCapacity);
                break;
            case ColumnDispatch.DispatchKey.Int64DateTimeOffset:
                EncodeRepeatedDateTimeOffset(Unsafe.As<ReadOnlySpan<T[]>, ReadOnlySpan<DateTimeOffset[]>>(ref rows),
                    ref state, column.Name, encodedBufferCapacity);
                break;
            case ColumnDispatch.DispatchKey.Int64TimeOnly:
                EncodeRepeatedTimeOnly(Unsafe.As<ReadOnlySpan<T[]>, ReadOnlySpan<TimeOnly[]>>(ref rows), ref state,
                    column.Name, encodedBufferCapacity);
                break;
            case ColumnDispatch.DispatchKey.ByteArrayString:
                EncodeRepeatedString(Unsafe.As<ReadOnlySpan<T[]>, ReadOnlySpan<string?[]>>(ref rows), ref state,
                    column.Name, encodedBufferCapacity);
                break;
            case ColumnDispatch.DispatchKey.ByteArrayByteArray:
                EncodeRepeatedByteArray(Unsafe.As<ReadOnlySpan<T[]>, ReadOnlySpan<byte[]?[]>>(ref rows), ref state,
                    column.Name, encodedBufferCapacity);
                break;
            case ColumnDispatch.DispatchKey.FloatFloat:
                EncodeRepeatedFloat(Unsafe.As<ReadOnlySpan<T[]>, ReadOnlySpan<float[]>>(ref rows), ref state,
                    column.Name, encodedBufferCapacity);
                break;
            case ColumnDispatch.DispatchKey.DoubleDouble:
                EncodeRepeatedDouble(Unsafe.As<ReadOnlySpan<T[]>, ReadOnlySpan<double[]>>(ref rows), ref state,
                    column.Name, encodedBufferCapacity);
                break;
            default:
                throw new InvalidOperationException(GetUnsupportedTypeMessage(column.Name, physicalType));
        }
    }

    static void EncodePlainRequired<T>(ReadOnlySpan<T> values, ColumnDispatch.DispatchKey dispatchKey,
        DateTimeKindHandling dateTimeKindHandling, ref ParquetWriter.RowGroupState.ColumnState state, string columnName,
        int encodedBufferCapacity, ParquetPhysicalType physicalType)
    {
        switch (dispatchKey)
        {
            case ColumnDispatch.DispatchKey.BooleanBool:
                Encoding.Plain.EncodeBoolean(Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<bool>>(ref values), ref state, columnName,
                    encodedBufferCapacity);
                return;
            case ColumnDispatch.DispatchKey.Int32Int32:
                Encoding.Plain.EncodeInt32(Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<int>>(ref values), ref state, columnName,
                    encodedBufferCapacity);
                return;
            case ColumnDispatch.DispatchKey.Int32DateOnly:
                Encoding.Plain.EncodeDateOnly(Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<DateOnly>>(ref values), ref state,
                    columnName, encodedBufferCapacity);
                return;
            case ColumnDispatch.DispatchKey.Int64Int64:
                Encoding.Plain.EncodeInt64(Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<long>>(ref values), ref state, columnName,
                    encodedBufferCapacity);
                return;
            case ColumnDispatch.DispatchKey.Int64DateTime:
                Encoding.Plain.EncodeDateTime(Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<DateTime>>(ref values),
                    dateTimeKindHandling, ref state, columnName, encodedBufferCapacity);
                return;
            case ColumnDispatch.DispatchKey.Int64DateTimeOffset:
                Encoding.Plain.EncodeDateTimeOffset(Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<DateTimeOffset>>(ref values),
                    ref state, columnName, encodedBufferCapacity);
                return;
            case ColumnDispatch.DispatchKey.Int64TimeOnly:
                Encoding.Plain.EncodeTimeOnly(Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<TimeOnly>>(ref values), ref state,
                    columnName, encodedBufferCapacity);
                return;
            case ColumnDispatch.DispatchKey.ByteArrayString:
                Encoding.Plain.EncodeString(Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<string>>(ref values), ref state, columnName,
                    encodedBufferCapacity);
                return;
            case ColumnDispatch.DispatchKey.ByteArrayByteArray:
                Encoding.Plain.EncodeByteArray(Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<byte[]>>(ref values), ref state,
                    columnName, encodedBufferCapacity);
                return;
            case ColumnDispatch.DispatchKey.FloatFloat:
                Encoding.Plain.EncodeFloat(Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<float>>(ref values), ref state, columnName,
                    encodedBufferCapacity);
                return;
            case ColumnDispatch.DispatchKey.DoubleDouble:
                Encoding.Plain.EncodeDouble(Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<double>>(ref values), ref state, columnName,
                    encodedBufferCapacity);
                return;
            default:
                throw new InvalidOperationException(GetUnsupportedTypeMessage(columnName, physicalType));
        }
    }

    static void EncodeDeltaBinaryPackedRequired<T>(ReadOnlySpan<T> values, ColumnDispatch.DispatchKey dispatchKey,
        DateTimeKindHandling dateTimeKindHandling, ref ParquetWriter.RowGroupState.ColumnState state, string columnName,
        int encodedBufferCapacity, ParquetPhysicalType physicalType)
    {
        switch (dispatchKey)
        {
            case ColumnDispatch.DispatchKey.Int32Int32:
                Encoding.DeltaBinaryPacked.EncodeInt32(Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<int>>(ref values), ref state, columnName, encodedBufferCapacity);
                return;
            case ColumnDispatch.DispatchKey.Int32DateOnly:
            {
                var source = Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<DateOnly>>(ref values);
                var buffer = ArrayPool<int>.Shared.Rent(source.Length);
                try
                {
                    var span = buffer.AsSpan(0, source.Length);
                    for (var i = 0; i < source.Length; i++)
                        span[i] = checked(source[i].DayNumber - UnixEpochDayNumber);
                    Encoding.DeltaBinaryPacked.EncodeInt32(span, ref state, columnName, encodedBufferCapacity);
                }
                finally
                {
                    ArrayPool<int>.Shared.Return(buffer);
                }
                return;
            }
            case ColumnDispatch.DispatchKey.Int64Int64:
                Encoding.DeltaBinaryPacked.EncodeInt64(Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<long>>(ref values), ref state, columnName, encodedBufferCapacity);
                return;
            case ColumnDispatch.DispatchKey.Int64DateTime:
            {
                var source = Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<DateTime>>(ref values);
                var buffer = ArrayPool<long>.Shared.Rent(source.Length);
                try
                {
                    var span = buffer.AsSpan(0, source.Length);
                    for (var i = 0; i < source.Length; i++)
                        span[i] = ToUnixMicroseconds(source[i], dateTimeKindHandling, columnName);
                    Encoding.DeltaBinaryPacked.EncodeInt64(span, ref state, columnName, encodedBufferCapacity);
                }
                finally
                {
                    ArrayPool<long>.Shared.Return(buffer);
                }
                return;
            }
            case ColumnDispatch.DispatchKey.Int64DateTimeOffset:
            {
                var source = Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<DateTimeOffset>>(ref values);
                var buffer = ArrayPool<long>.Shared.Rent(source.Length);
                try
                {
                    var span = buffer.AsSpan(0, source.Length);
                    for (var i = 0; i < source.Length; i++)
                    {
                        var deltaTicks = checked(source[i].UtcTicks - UnixEpochTicks);
                        span[i] = deltaTicks / TicksPerMicrosecond;
                    }
                    Encoding.DeltaBinaryPacked.EncodeInt64(span, ref state, columnName, encodedBufferCapacity);
                }
                finally
                {
                    ArrayPool<long>.Shared.Return(buffer);
                }
                return;
            }
            case ColumnDispatch.DispatchKey.Int64TimeOnly:
            {
                var source = Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<TimeOnly>>(ref values);
                var buffer = ArrayPool<long>.Shared.Rent(source.Length);
                try
                {
                    var span = buffer.AsSpan(0, source.Length);
                    for (var i = 0; i < source.Length; i++)
                        span[i] = source[i].Ticks / TicksPerMicrosecond;
                    Encoding.DeltaBinaryPacked.EncodeInt64(span, ref state, columnName, encodedBufferCapacity);
                }
                finally
                {
                    ArrayPool<long>.Shared.Return(buffer);
                }
                return;
            }
            default:
                throw new InvalidOperationException(GetUnsupportedTypeMessage(columnName, physicalType));
        }
    }

    static void EncodeDeltaLengthByteArrayRequired<T>(ReadOnlySpan<T> values, ColumnDispatch.DispatchKey dispatchKey,
        ref ParquetWriter.RowGroupState.ColumnState state, string columnName, int encodedBufferCapacity, ParquetPhysicalType physicalType)
    {
        switch (dispatchKey)
        {
            case ColumnDispatch.DispatchKey.ByteArrayString:
                Encoding.DeltaLengthByteArray.EncodeString(Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<string>>(ref values), ref state, columnName, encodedBufferCapacity);
                return;
            case ColumnDispatch.DispatchKey.ByteArrayByteArray:
                Encoding.DeltaLengthByteArray.EncodeByteArray(Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<byte[]>>(ref values), ref state, columnName, encodedBufferCapacity);
                return;
            default:
                throw new InvalidOperationException(GetUnsupportedTypeMessage(columnName, physicalType));
        }
    }

    static void EncodeDeltaByteArrayRequired<T>(ReadOnlySpan<T> values, ColumnDispatch.DispatchKey dispatchKey,
        ref ParquetWriter.RowGroupState.ColumnState state, string columnName, int encodedBufferCapacity, ParquetPhysicalType physicalType)
    {
        switch (dispatchKey)
        {
            case ColumnDispatch.DispatchKey.ByteArrayString:
                Encoding.DeltaByteArray.EncodeString(Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<string>>(ref values), ref state, columnName, encodedBufferCapacity);
                return;
            case ColumnDispatch.DispatchKey.ByteArrayByteArray:
                Encoding.DeltaByteArray.EncodeByteArray(Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<byte[]>>(ref values), ref state, columnName, encodedBufferCapacity);
                return;
            default:
                throw new InvalidOperationException(GetUnsupportedTypeMessage(columnName, physicalType));
        }
    }

    static void EncodeByteStreamSplitRequired<T>(ReadOnlySpan<T> values, ColumnDispatch.DispatchKey dispatchKey,
        DateTimeKindHandling dateTimeKindHandling, ref ParquetWriter.RowGroupState.ColumnState state, string columnName,
        int encodedBufferCapacity, ParquetPhysicalType physicalType)
    {
        switch (dispatchKey)
        {
            case ColumnDispatch.DispatchKey.Int32Int32:
                Encoding.ByteStreamSplit.EncodeInt32(Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<int>>(ref values), ref state, columnName, encodedBufferCapacity);
                return;
            case ColumnDispatch.DispatchKey.Int32DateOnly:
            {
                var source = Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<DateOnly>>(ref values);
                var buffer = ArrayPool<int>.Shared.Rent(source.Length);
                try
                {
                    var span = buffer.AsSpan(0, source.Length);
                    for (var i = 0; i < source.Length; i++)
                        span[i] = checked(source[i].DayNumber - UnixEpochDayNumber);
                    Encoding.ByteStreamSplit.EncodeInt32(span, ref state, columnName, encodedBufferCapacity);
                }
                finally
                {
                    ArrayPool<int>.Shared.Return(buffer);
                }
                return;
            }
            case ColumnDispatch.DispatchKey.Int64Int64:
                Encoding.ByteStreamSplit.EncodeInt64(Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<long>>(ref values), ref state, columnName, encodedBufferCapacity);
                return;
            case ColumnDispatch.DispatchKey.Int64DateTime:
            {
                var source = Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<DateTime>>(ref values);
                var buffer = ArrayPool<long>.Shared.Rent(source.Length);
                try
                {
                    var span = buffer.AsSpan(0, source.Length);
                    for (var i = 0; i < source.Length; i++)
                        span[i] = ToUnixMicroseconds(source[i], dateTimeKindHandling, columnName);
                    Encoding.ByteStreamSplit.EncodeInt64(span, ref state, columnName, encodedBufferCapacity);
                }
                finally
                {
                    ArrayPool<long>.Shared.Return(buffer);
                }
                return;
            }
            case ColumnDispatch.DispatchKey.Int64DateTimeOffset:
            {
                var source = Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<DateTimeOffset>>(ref values);
                var buffer = ArrayPool<long>.Shared.Rent(source.Length);
                try
                {
                    var span = buffer.AsSpan(0, source.Length);
                    for (var i = 0; i < source.Length; i++)
                    {
                        var deltaTicks = checked(source[i].UtcTicks - UnixEpochTicks);
                        span[i] = deltaTicks / TicksPerMicrosecond;
                    }
                    Encoding.ByteStreamSplit.EncodeInt64(span, ref state, columnName, encodedBufferCapacity);
                }
                finally
                {
                    ArrayPool<long>.Shared.Return(buffer);
                }
                return;
            }
            case ColumnDispatch.DispatchKey.Int64TimeOnly:
            {
                var source = Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<TimeOnly>>(ref values);
                var buffer = ArrayPool<long>.Shared.Rent(source.Length);
                try
                {
                    var span = buffer.AsSpan(0, source.Length);
                    for (var i = 0; i < source.Length; i++)
                        span[i] = source[i].Ticks / TicksPerMicrosecond;
                    Encoding.ByteStreamSplit.EncodeInt64(span, ref state, columnName, encodedBufferCapacity);
                }
                finally
                {
                    ArrayPool<long>.Shared.Return(buffer);
                }
                return;
            }
            case ColumnDispatch.DispatchKey.FloatFloat:
                Encoding.ByteStreamSplit.EncodeFloat(Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<float>>(ref values), ref state, columnName, encodedBufferCapacity);
                return;
            case ColumnDispatch.DispatchKey.DoubleDouble:
                Encoding.ByteStreamSplit.EncodeDouble(Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<double>>(ref values), ref state, columnName, encodedBufferCapacity);
                return;
            default:
                throw new InvalidOperationException(GetUnsupportedTypeMessage(columnName, physicalType));
        }
    }
}
