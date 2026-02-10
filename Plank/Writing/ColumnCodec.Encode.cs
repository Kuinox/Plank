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

        if (!TryEncodeNonRepeated(values, encoding, dispatchKey, dateTimeKindHandling, ref state, column.Name,
                encodedBufferCapacity))
        {
            if (encoding is EncodingKind.Plain or EncodingKind.DeltaBinaryPacked or EncodingKind.DeltaLengthByteArray
                or EncodingKind.DeltaByteArray or EncodingKind.ByteStreamSplit)
                throw new InvalidOperationException(GetUnsupportedTypeMessage(column.Name, physicalType));

            throw new NotSupportedException(
                $"Encoding '{encoding}' with value type '{typeof(T)}' is not supported for column '{column.Name}'.");
        }

        state.NullCount = 0;
        state.DefinitionLevelsByteLength = 0;
        state.RepetitionLevelsByteLength = 0;
    }

    static bool TryEncodeNonRepeated<T>(ReadOnlySpan<T> values, EncodingKind encoding,
        ColumnDispatch.DispatchKey dispatchKey, DateTimeKindHandling dateTimeKindHandling,
        ref ParquetWriter.RowGroupState.ColumnState state, string columnName, int encodedBufferCapacity)
    {
        switch (encoding)
        {
            case EncodingKind.Plain:
                return TryEncodePlainNonRepeated(values, dispatchKey, dateTimeKindHandling, ref state, columnName,
                    encodedBufferCapacity);
            case EncodingKind.DeltaBinaryPacked:
                return TryEncodeDeltaBinaryPackedNonRepeated(values, dispatchKey, dateTimeKindHandling, ref state,
                    columnName, encodedBufferCapacity);
            case EncodingKind.DeltaLengthByteArray:
                return TryEncodeDeltaLengthByteArrayNonRepeated(values, dispatchKey, ref state, columnName,
                    encodedBufferCapacity);
            case EncodingKind.DeltaByteArray:
                return TryEncodeDeltaByteArrayNonRepeated(values, dispatchKey, ref state, columnName,
                    encodedBufferCapacity);
            case EncodingKind.ByteStreamSplit:
                return TryEncodeByteStreamSplitNonRepeated(values, dispatchKey, dateTimeKindHandling, ref state,
                    columnName, encodedBufferCapacity);
            default:
                return false;
        }
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
        if (TryEncodeRepeated(rows, dispatchKey, dateTimeKindHandling, ref state, column.Name, encodedBufferCapacity))
            return;

        throw new InvalidOperationException(GetUnsupportedTypeMessage(column.Name, physicalType));
    }

    static bool TryEncodeRepeated<T>(ReadOnlySpan<T[]> rows, ColumnDispatch.DispatchKey dispatchKey,
        DateTimeKindHandling dateTimeKindHandling, ref ParquetWriter.RowGroupState.ColumnState state, string columnName,
        int encodedBufferCapacity)
    {
        switch (dispatchKey)
        {
            case ColumnDispatch.DispatchKey.BooleanBool:
                EncodeRepeatedBoolean(Unsafe.As<ReadOnlySpan<T[]>, ReadOnlySpan<bool[]>>(ref rows), ref state,
                    columnName, encodedBufferCapacity);
                return true;
            case ColumnDispatch.DispatchKey.Int32Int32:
                EncodeRepeatedRequired<int, RepeatedInt32Writer>(
                    Unsafe.As<ReadOnlySpan<T[]>, ReadOnlySpan<int[]>>(ref rows), ref state, columnName,
                    encodedBufferCapacity, DateTimeKindHandling.None);
                return true;
            case ColumnDispatch.DispatchKey.Int32NullableInt32:
                EncodeRepeatedOptionalNullableStruct<int, RepeatedOptionalInt32Writer>(
                    Unsafe.As<ReadOnlySpan<T[]>, ReadOnlySpan<int?[]>>(ref rows), ref state, columnName,
                    encodedBufferCapacity, DateTimeKindHandling.None);
                return true;
            case ColumnDispatch.DispatchKey.Int32DateOnly:
                EncodeRepeatedRequired<DateOnly, RepeatedDateOnlyWriter>(
                    Unsafe.As<ReadOnlySpan<T[]>, ReadOnlySpan<DateOnly[]>>(ref rows), ref state, columnName,
                    encodedBufferCapacity, DateTimeKindHandling.None);
                return true;
            case ColumnDispatch.DispatchKey.Int64Int64:
                EncodeRepeatedRequired<long, RepeatedInt64Writer>(
                    Unsafe.As<ReadOnlySpan<T[]>, ReadOnlySpan<long[]>>(ref rows), ref state, columnName,
                    encodedBufferCapacity, DateTimeKindHandling.None);
                return true;
            case ColumnDispatch.DispatchKey.Int64DateTime:
                ValidateDateTimeHandling(dateTimeKindHandling, columnName);
                EncodeRepeatedRequired<DateTime, RepeatedDateTimeWriter>(
                    Unsafe.As<ReadOnlySpan<T[]>, ReadOnlySpan<DateTime[]>>(ref rows), ref state, columnName,
                    encodedBufferCapacity, dateTimeKindHandling);
                return true;
            case ColumnDispatch.DispatchKey.Int64DateTimeOffset:
                EncodeRepeatedRequired<DateTimeOffset, RepeatedDateTimeOffsetWriter>(
                    Unsafe.As<ReadOnlySpan<T[]>, ReadOnlySpan<DateTimeOffset[]>>(ref rows), ref state, columnName,
                    encodedBufferCapacity, DateTimeKindHandling.None);
                return true;
            case ColumnDispatch.DispatchKey.Int64TimeOnly:
                EncodeRepeatedRequired<TimeOnly, RepeatedTimeOnlyWriter>(
                    Unsafe.As<ReadOnlySpan<T[]>, ReadOnlySpan<TimeOnly[]>>(ref rows), ref state, columnName,
                    encodedBufferCapacity, DateTimeKindHandling.None);
                return true;
            case ColumnDispatch.DispatchKey.ByteArrayString:
                EncodeRepeatedOptionalReference<string, RepeatedStringReferenceWriter>(
                    Unsafe.As<ReadOnlySpan<T[]>, ReadOnlySpan<string?[]>>(ref rows), ref state, columnName,
                    encodedBufferCapacity);
                return true;
            case ColumnDispatch.DispatchKey.ByteArrayByteArray:
                EncodeRepeatedOptionalReference<byte[], RepeatedByteArrayReferenceWriter>(
                    Unsafe.As<ReadOnlySpan<T[]>, ReadOnlySpan<byte[]?[]>>(ref rows), ref state, columnName,
                    encodedBufferCapacity);
                return true;
            case ColumnDispatch.DispatchKey.FloatFloat:
                EncodeRepeatedRequired<float, RepeatedFloatWriter>(
                    Unsafe.As<ReadOnlySpan<T[]>, ReadOnlySpan<float[]>>(ref rows), ref state, columnName,
                    encodedBufferCapacity, DateTimeKindHandling.None);
                return true;
            case ColumnDispatch.DispatchKey.DoubleDouble:
                EncodeRepeatedRequired<double, RepeatedDoubleWriter>(
                    Unsafe.As<ReadOnlySpan<T[]>, ReadOnlySpan<double[]>>(ref rows), ref state, columnName,
                    encodedBufferCapacity, DateTimeKindHandling.None);
                return true;
            default:
                return false;
        }
    }

    static bool TryEncodePlainNonRepeated<T>(ReadOnlySpan<T> values, ColumnDispatch.DispatchKey dispatchKey,
        DateTimeKindHandling dateTimeKindHandling, ref ParquetWriter.RowGroupState.ColumnState state, string columnName,
        int encodedBufferCapacity)
    {
        switch (dispatchKey)
        {
            case ColumnDispatch.DispatchKey.BooleanBool:
                Encoding.Plain.EncodeBoolean(Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<bool>>(ref values), ref state, columnName,
                    encodedBufferCapacity);
                return true;
            case ColumnDispatch.DispatchKey.Int32Int32:
                Encoding.Plain.EncodeInt32(Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<int>>(ref values), ref state, columnName,
                    encodedBufferCapacity);
                return true;
            case ColumnDispatch.DispatchKey.Int32DateOnly:
                Encoding.Plain.EncodeDateOnly(Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<DateOnly>>(ref values), ref state,
                    columnName, encodedBufferCapacity);
                return true;
            case ColumnDispatch.DispatchKey.Int64Int64:
                Encoding.Plain.EncodeInt64(Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<long>>(ref values), ref state, columnName,
                    encodedBufferCapacity);
                return true;
            case ColumnDispatch.DispatchKey.Int64DateTime:
                Encoding.Plain.EncodeDateTime(Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<DateTime>>(ref values),
                    dateTimeKindHandling, ref state, columnName, encodedBufferCapacity);
                return true;
            case ColumnDispatch.DispatchKey.Int64DateTimeOffset:
                Encoding.Plain.EncodeDateTimeOffset(Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<DateTimeOffset>>(ref values),
                    ref state, columnName, encodedBufferCapacity);
                return true;
            case ColumnDispatch.DispatchKey.Int64TimeOnly:
                Encoding.Plain.EncodeTimeOnly(Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<TimeOnly>>(ref values), ref state,
                    columnName, encodedBufferCapacity);
                return true;
            case ColumnDispatch.DispatchKey.ByteArrayString:
                Encoding.Plain.EncodeString(Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<string>>(ref values), ref state, columnName,
                    encodedBufferCapacity);
                return true;
            case ColumnDispatch.DispatchKey.ByteArrayByteArray:
                Encoding.Plain.EncodeByteArray(Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<byte[]>>(ref values), ref state,
                    columnName, encodedBufferCapacity);
                return true;
            case ColumnDispatch.DispatchKey.FloatFloat:
                Encoding.Plain.EncodeFloat(Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<float>>(ref values), ref state, columnName,
                    encodedBufferCapacity);
                return true;
            case ColumnDispatch.DispatchKey.DoubleDouble:
                Encoding.Plain.EncodeDouble(Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<double>>(ref values), ref state, columnName,
                    encodedBufferCapacity);
                return true;
            default:
                return false;
        }
    }

    static bool TryEncodeDeltaBinaryPackedNonRepeated<T>(ReadOnlySpan<T> values, ColumnDispatch.DispatchKey dispatchKey,
        DateTimeKindHandling dateTimeKindHandling, ref ParquetWriter.RowGroupState.ColumnState state, string columnName,
        int encodedBufferCapacity)
        => TryEncodeDeltaOrByteStreamSplitNonRepeated(values, dispatchKey, dateTimeKindHandling, ref state, columnName,
            encodedBufferCapacity, byteStreamSplit: false);

    static bool TryEncodeDeltaLengthByteArrayNonRepeated<T>(ReadOnlySpan<T> values, ColumnDispatch.DispatchKey dispatchKey,
        ref ParquetWriter.RowGroupState.ColumnState state, string columnName, int encodedBufferCapacity)
        => TryEncodeDeltaVariableByteArrayNonRepeated(values, dispatchKey, ref state, columnName,
            encodedBufferCapacity, deltaByteArray: false);

    static bool TryEncodeDeltaByteArrayNonRepeated<T>(ReadOnlySpan<T> values, ColumnDispatch.DispatchKey dispatchKey,
        ref ParquetWriter.RowGroupState.ColumnState state, string columnName, int encodedBufferCapacity)
        => TryEncodeDeltaVariableByteArrayNonRepeated(values, dispatchKey, ref state, columnName,
            encodedBufferCapacity, deltaByteArray: true);

    static bool TryEncodeDeltaVariableByteArrayNonRepeated<T>(ReadOnlySpan<T> values,
        ColumnDispatch.DispatchKey dispatchKey, ref ParquetWriter.RowGroupState.ColumnState state, string columnName,
        int encodedBufferCapacity, bool deltaByteArray)
    {
        switch (dispatchKey)
        {
            case ColumnDispatch.DispatchKey.ByteArrayString:
                if (deltaByteArray)
                    Encoding.DeltaByteArray.EncodeString(Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<string>>(ref values),
                        ref state, columnName, encodedBufferCapacity);
                else
                    Encoding.DeltaLengthByteArray.EncodeString(
                        Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<string>>(ref values), ref state, columnName,
                        encodedBufferCapacity);
                return true;
            case ColumnDispatch.DispatchKey.ByteArrayByteArray:
                if (deltaByteArray)
                    Encoding.DeltaByteArray.EncodeByteArray(
                        Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<byte[]>>(ref values), ref state, columnName,
                        encodedBufferCapacity);
                else
                    Encoding.DeltaLengthByteArray.EncodeByteArray(
                        Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<byte[]>>(ref values), ref state, columnName,
                        encodedBufferCapacity);
                return true;
            default:
                return false;
        }
    }

    static bool TryEncodeByteStreamSplitNonRepeated<T>(ReadOnlySpan<T> values, ColumnDispatch.DispatchKey dispatchKey,
        DateTimeKindHandling dateTimeKindHandling, ref ParquetWriter.RowGroupState.ColumnState state, string columnName,
        int encodedBufferCapacity)
        => TryEncodeDeltaOrByteStreamSplitNonRepeated(values, dispatchKey, dateTimeKindHandling, ref state, columnName,
            encodedBufferCapacity, byteStreamSplit: true);

    static bool TryEncodeDeltaOrByteStreamSplitNonRepeated<T>(ReadOnlySpan<T> values,
        ColumnDispatch.DispatchKey dispatchKey, DateTimeKindHandling dateTimeKindHandling,
        ref ParquetWriter.RowGroupState.ColumnState state, string columnName, int encodedBufferCapacity,
        bool byteStreamSplit)
    {
        switch (dispatchKey)
        {
            case ColumnDispatch.DispatchKey.Int32Int32:
                EncodeInt32ForNumericEncodings(Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<int>>(ref values), byteStreamSplit,
                    ref state, columnName, encodedBufferCapacity);
                return true;
            case ColumnDispatch.DispatchKey.Int32DateOnly:
                EncodeDateOnlyForNumericEncodings(Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<DateOnly>>(ref values),
                    byteStreamSplit, ref state, columnName, encodedBufferCapacity);
                return true;
            case ColumnDispatch.DispatchKey.Int64Int64:
                EncodeInt64ForNumericEncodings(Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<long>>(ref values), byteStreamSplit,
                    ref state, columnName, encodedBufferCapacity);
                return true;
            case ColumnDispatch.DispatchKey.Int64DateTime:
                EncodeDateTimeForNumericEncodings(Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<DateTime>>(ref values),
                    dateTimeKindHandling, byteStreamSplit, ref state, columnName, encodedBufferCapacity);
                return true;
            case ColumnDispatch.DispatchKey.Int64DateTimeOffset:
                EncodeDateTimeOffsetForNumericEncodings(
                    Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<DateTimeOffset>>(ref values), byteStreamSplit, ref state,
                    columnName, encodedBufferCapacity);
                return true;
            case ColumnDispatch.DispatchKey.Int64TimeOnly:
                EncodeTimeOnlyForNumericEncodings(Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<TimeOnly>>(ref values),
                    byteStreamSplit, ref state, columnName, encodedBufferCapacity);
                return true;
            case ColumnDispatch.DispatchKey.FloatFloat:
                if (!byteStreamSplit)
                    return false;
                Encoding.ByteStreamSplit.EncodeFloat(Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<float>>(ref values), ref state, columnName, encodedBufferCapacity);
                return true;
            case ColumnDispatch.DispatchKey.DoubleDouble:
                if (!byteStreamSplit)
                    return false;
                Encoding.ByteStreamSplit.EncodeDouble(Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<double>>(ref values), ref state, columnName, encodedBufferCapacity);
                return true;
            default:
                return false;
        }
    }

    static void EncodeInt32ForNumericEncodings(ReadOnlySpan<int> values, bool byteStreamSplit,
        ref ParquetWriter.RowGroupState.ColumnState state, string columnName, int encodedBufferCapacity)
    {
        if (byteStreamSplit)
            Encoding.ByteStreamSplit.EncodeInt32(values, ref state, columnName, encodedBufferCapacity);
        else
            Encoding.DeltaBinaryPacked.EncodeInt32(values, ref state, columnName, encodedBufferCapacity);
    }

    static void EncodeInt64ForNumericEncodings(ReadOnlySpan<long> values, bool byteStreamSplit,
        ref ParquetWriter.RowGroupState.ColumnState state, string columnName, int encodedBufferCapacity)
    {
        if (byteStreamSplit)
            Encoding.ByteStreamSplit.EncodeInt64(values, ref state, columnName, encodedBufferCapacity);
        else
            Encoding.DeltaBinaryPacked.EncodeInt64(values, ref state, columnName, encodedBufferCapacity);
    }

    static void EncodeDateOnlyForNumericEncodings(ReadOnlySpan<DateOnly> source, bool byteStreamSplit,
        ref ParquetWriter.RowGroupState.ColumnState state, string columnName, int encodedBufferCapacity)
    {
        var buffer = ArrayPool<int>.Shared.Rent(source.Length);
        try
        {
            var span = buffer.AsSpan(0, source.Length);
            for (var i = 0; i < source.Length; i++)
                span[i] = checked(source[i].DayNumber - UnixEpochDayNumber);
            EncodeInt32ForNumericEncodings(span, byteStreamSplit, ref state, columnName, encodedBufferCapacity);
        }
        finally
        {
            ArrayPool<int>.Shared.Return(buffer);
        }
    }

    static void EncodeDateTimeForNumericEncodings(ReadOnlySpan<DateTime> source, DateTimeKindHandling dateTimeKindHandling,
        bool byteStreamSplit, ref ParquetWriter.RowGroupState.ColumnState state, string columnName, int encodedBufferCapacity)
    {
        var buffer = ArrayPool<long>.Shared.Rent(source.Length);
        try
        {
            var span = buffer.AsSpan(0, source.Length);
            for (var i = 0; i < source.Length; i++)
                span[i] = ToUnixMicroseconds(source[i], dateTimeKindHandling, columnName);
            EncodeInt64ForNumericEncodings(span, byteStreamSplit, ref state, columnName, encodedBufferCapacity);
        }
        finally
        {
            ArrayPool<long>.Shared.Return(buffer);
        }
    }

    static void EncodeDateTimeOffsetForNumericEncodings(ReadOnlySpan<DateTimeOffset> source, bool byteStreamSplit,
        ref ParquetWriter.RowGroupState.ColumnState state, string columnName, int encodedBufferCapacity)
    {
        var buffer = ArrayPool<long>.Shared.Rent(source.Length);
        try
        {
            var span = buffer.AsSpan(0, source.Length);
            for (var i = 0; i < source.Length; i++)
            {
                var deltaTicks = checked(source[i].UtcTicks - UnixEpochTicks);
                span[i] = deltaTicks / TicksPerMicrosecond;
            }
            EncodeInt64ForNumericEncodings(span, byteStreamSplit, ref state, columnName, encodedBufferCapacity);
        }
        finally
        {
            ArrayPool<long>.Shared.Return(buffer);
        }
    }

    static void EncodeTimeOnlyForNumericEncodings(ReadOnlySpan<TimeOnly> source, bool byteStreamSplit,
        ref ParquetWriter.RowGroupState.ColumnState state, string columnName, int encodedBufferCapacity)
    {
        var buffer = ArrayPool<long>.Shared.Rent(source.Length);
        try
        {
            var span = buffer.AsSpan(0, source.Length);
            for (var i = 0; i < source.Length; i++)
                span[i] = source[i].Ticks / TicksPerMicrosecond;
            EncodeInt64ForNumericEncodings(span, byteStreamSplit, ref state, columnName, encodedBufferCapacity);
        }
        finally
        {
            ArrayPool<long>.Shared.Return(buffer);
        }
    }
}
