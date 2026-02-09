using Plank.Schema;
using System.Runtime.CompilerServices;

namespace Plank.Writing;

static partial class ColumnCodec
{
    internal static void Encode<T>(Column column, ReadOnlySpan<T> values, ParquetPhysicalType physicalType,
        DateTimeKindHandling dateTimeKindHandling, ref ParquetWriter.RowGroupState.ColumnState state)
    {
        var encoding = ResolveDefaultEncoding(column.Options.Encodings);
        if (encoding != EncodingKind.Plain)
            throw new NotSupportedException($"Encoding '{encoding}' is not supported for column '{column.Name}'.");

        var encodedBufferCapacity = GetDestinationCapacity(ref state);
        state.Encoding = encoding;
        var valueKind = ColumnDispatch.GetValueKind<T>();
        var dispatchKey = ColumnDispatch.GetDispatchKey(physicalType, valueKind);
        if (column.Options.Repetition is ParquetRepetition.Optional)
        {
            if (TryEncodeOptional(column, values, dispatchKey, encodedBufferCapacity, dateTimeKindHandling, ref state))
                return;

            throw new NotSupportedException(
                $"Optional column '{column.Name}' is not supported for value type '{typeof(T)}' yet.");
        }

        switch (dispatchKey)
        {
            case ColumnDispatch.DispatchKey.BooleanBool:
                EncodePlainBoolean(Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<bool>>(ref values), ref state, column.Name,
                    encodedBufferCapacity);
                break;
            case ColumnDispatch.DispatchKey.Int32Int32:
                EncodePlainInt32(Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<int>>(ref values), ref state, column.Name,
                    encodedBufferCapacity);
                break;
            case ColumnDispatch.DispatchKey.Int32DateOnly:
                EncodePlainDateOnly(Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<DateOnly>>(ref values), ref state,
                    column.Name, encodedBufferCapacity);
                break;
            case ColumnDispatch.DispatchKey.Int64Int64:
                EncodePlainInt64(Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<long>>(ref values), ref state, column.Name,
                    encodedBufferCapacity);
                break;
            case ColumnDispatch.DispatchKey.Int64DateTime:
                EncodePlainDateTime(Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<DateTime>>(ref values),
                    dateTimeKindHandling, ref state, column.Name, encodedBufferCapacity);
                break;
            case ColumnDispatch.DispatchKey.Int64DateTimeOffset:
                EncodePlainDateTimeOffset(Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<DateTimeOffset>>(ref values),
                    ref state, column.Name, encodedBufferCapacity);
                break;
            case ColumnDispatch.DispatchKey.Int64TimeOnly:
                EncodePlainTimeOnly(Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<TimeOnly>>(ref values), ref state,
                    column.Name, encodedBufferCapacity);
                break;
            case ColumnDispatch.DispatchKey.ByteArrayString:
                EncodePlainString(Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<string>>(ref values), ref state, column.Name,
                    encodedBufferCapacity);
                break;
            case ColumnDispatch.DispatchKey.ByteArrayByteArray:
                EncodePlainByteArray(Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<byte[]>>(ref values), ref state,
                    column.Name, encodedBufferCapacity);
                break;
            case ColumnDispatch.DispatchKey.FloatFloat:
                EncodePlainFloat(Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<float>>(ref values), ref state, column.Name,
                    encodedBufferCapacity);
                break;
            case ColumnDispatch.DispatchKey.DoubleDouble:
                EncodePlainDouble(Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<double>>(ref values), ref state, column.Name,
                    encodedBufferCapacity);
                break;
            default:
                throw new InvalidOperationException(GetUnsupportedTypeMessage(column.Name, physicalType));
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
}
