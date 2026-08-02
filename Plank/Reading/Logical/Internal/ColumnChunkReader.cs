using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Plank.Schema;

namespace Plank.Reading.Logical.Internal;

static class ColumnChunkReader
{
    static readonly DateOnly UnixEpochDate = new(1970, 1, 1);

    internal static bool TryDecodeDictionaryPageIntoNative<T>(PageHeader header, ReadOnlySpan<byte> payload,
        Column column, ref ColumnReadBuffers<T> state, IParquetBufferPool bufferPool)
    {
        if (header.Type != PageHeaderType.DictionaryPage)
            return false;

        if (typeof(T) == typeof(BinaryValueDescriptor))
            return TryDecodeBinaryDictionaryPage(header, payload, column, ref state, bufferPool);
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            return false;

        var physicalType = GetPhysicalDecodeType<T>();
        var decoded = TryDecodeDictionaryByPhysicalType(header, payload, column, physicalType,
            ref state, bufferPool);
        if (decoded)
            return true;

        state.Dictionary.Dispose();
        state.DictionaryCount = 0;
        state.HasDictionary = false;
        return false;
    }

    static bool TryDecodeDictionaryByPhysicalType<T>(PageHeader header, ReadOnlySpan<byte> payload,
        Column column, Type physicalType, ref ColumnReadBuffers<T> state, IParquetBufferPool bufferPool)
    {
        if (physicalType == typeof(int))
            return TryDecodeDictionaryIntoNative<T, int>(header, payload, column, ref state, bufferPool);
        if (physicalType == typeof(long))
            return TryDecodeDictionaryIntoNative<T, long>(header, payload, column, ref state, bufferPool);
        if (physicalType == typeof(bool))
            return TryDecodeDictionaryIntoNative<T, bool>(header, payload, column, ref state, bufferPool);
        if (physicalType == typeof(float))
            return TryDecodeDictionaryIntoNative<T, float>(header, payload, column, ref state, bufferPool);
        if (physicalType == typeof(double))
            return TryDecodeDictionaryIntoNative<T, double>(header, payload, column, ref state, bufferPool);
        if (physicalType == typeof(byte))
            return TryDecodeDictionaryIntoNative<T, byte>(header, payload, column, ref state, bufferPool);
        if (physicalType == typeof(ushort))
            return TryDecodeDictionaryIntoNative<T, ushort>(header, payload, column, ref state, bufferPool);
        if (physicalType == typeof(uint))
            return TryDecodeDictionaryIntoNative<T, uint>(header, payload, column, ref state, bufferPool);
        if (physicalType == typeof(ulong))
            return TryDecodeDictionaryIntoNative<T, ulong>(header, payload, column, ref state, bufferPool);
        if (physicalType == typeof(DateOnly))
            return TryDecodeDictionaryIntoNative<T, DateOnly>(header, payload, column, ref state, bufferPool);
        if (physicalType == typeof(DateTime))
            return TryDecodeDictionaryIntoNative<T, DateTime>(header, payload, column, ref state, bufferPool);
        if (physicalType == typeof(DateTimeOffset))
            return TryDecodeDictionaryIntoNative<T, DateTimeOffset>(header, payload, column, ref state, bufferPool);
        if (physicalType == typeof(TimeOnly))
            return TryDecodeDictionaryIntoNative<T, TimeOnly>(header, payload, column, ref state, bufferPool);
        return false;
    }

    static bool TryDecodeDictionaryIntoNative<TPage, TValue>(PageHeader header, ReadOnlySpan<byte> payload,
        Column column, ref ColumnReadBuffers<TPage> state, IParquetBufferPool bufferPool)
    {
        var valueCount = checked((int)header.ValueCount);
        var destination = state.GetDictionary<TValue>(valueCount, bufferPool);
        return TryDecodeValuesIntoNative(payload, column, header.ValueCount, header.Encoding, destination);
    }

    internal static bool TryDecodeNullablePageIntoNative<T>(PageHeader header, ReadOnlySpan<byte> payload,
        Column column, ulong rowCount, ref ColumnReadBuffers<T> state, IParquetBufferPool bufferPool,
        out ColumnBuffer<T> buffer)
    {
        buffer = default;
        if (typeof(T) == typeof(BinaryValueDescriptor) &&
            column.Options.Repetition == ParquetRepetition.Optional)
            return TryDecodeBinaryDataPage(header, payload, column, rowCount, ref state, bufferPool,
                optional: true, out buffer);

        var physicalType = GetPhysicalDecodeType<T>();
        if (header.Type is not (PageHeaderType.DataPage or PageHeaderType.DataPageV2) ||
            physicalType == typeof(T) ||
            RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            return false;
        if (header.ValueCount > rowCount)
            throw new CorruptParquetException(
                $"Page value count ({header.ValueCount}) exceeds row group row count ({rowCount}).");

        ReadOnlySpan<byte> definitionPayload;
        ReadOnlySpan<byte> dataPayload;
        var expectedPhysicalCount = header.ValueCount;
        if (header.Type == PageHeaderType.DataPageV2)
        {
            if (header.NullCount > header.ValueCount)
                throw new CorruptParquetException(
                    $"Page null count ({header.NullCount}) exceeds value count ({header.ValueCount}).");
            var levelBytes = checked((int)(header.RepetitionLevelsByteLength +
                header.DefinitionLevelsByteLength));
            if ((uint)levelBytes > (uint)payload.Length)
                throw new CorruptParquetException(
                    $"Level bytes ({levelBytes}) exceed page payload size ({payload.Length}).");
            definitionPayload = header.DefinitionLevelsByteLength == 0
                ? []
                : payload.Slice(checked((int)header.RepetitionLevelsByteLength),
                    checked((int)header.DefinitionLevelsByteLength));
            dataPayload = payload[levelBytes..];
            expectedPhysicalCount -= header.NullCount;
        }
        else if (column.Options.Repetition == ParquetRepetition.Optional)
        {
            var definitionLength = GetDataPageV1LevelPayloadLength(payload, header.ValueCount,
                header.DefinitionLevelEncoding, bitWidth: 1, "definition", out var definitionOffset);
            definitionPayload = payload.Slice(definitionOffset, definitionLength);
            dataPayload = payload[(definitionOffset + definitionLength)..];
        }
        else
        {
            definitionPayload = [];
            dataPayload = payload;
        }

        var valueCount = checked((int)header.ValueCount);
        var physicalCount = valueCount;
        if (!definitionPayload.IsEmpty)
        {
            DecodeDefinitionLevels(definitionPayload, valueCount, header.DefinitionLevelEncoding,
                [], out physicalCount);
            if (header.Type == PageHeaderType.DataPageV2 && physicalCount != expectedPhysicalCount)
                throw new CorruptParquetException(
                    $"Definition levels contain {physicalCount} values, expected {expectedPhysicalCount}.");
        }

        var decoded = TryDecodeNullableValuesByPhysicalType(dataPayload, definitionPayload, valueCount,
            physicalCount, column, header.Encoding,
            header.Type == PageHeaderType.DataPage ? header.DefinitionLevelEncoding : EncodingKind.Rle,
            physicalType, ref state, bufferPool);
        if (!decoded)
            return false;

        buffer = state.CreateNativeBuffer(valueCount);
        return true;
    }

    static bool TryDecodeNullableValuesByPhysicalType<T>(ReadOnlySpan<byte> payload,
        ReadOnlySpan<byte> definitionPayload, int valueCount, int physicalCount, Column column,
        EncodingKind encoding, EncodingKind definitionLevelEncoding, Type physicalType, ref ColumnReadBuffers<T> state,
        IParquetBufferPool bufferPool)
    {
        if (physicalType == typeof(int))
            return TryDecodeNullableValues<T, int>(payload, definitionPayload, valueCount, physicalCount,
                column, encoding, definitionLevelEncoding, ref state, bufferPool);
        if (physicalType == typeof(long))
            return TryDecodeNullableValues<T, long>(payload, definitionPayload, valueCount, physicalCount,
                column, encoding, definitionLevelEncoding, ref state, bufferPool);
        if (physicalType == typeof(bool))
            return TryDecodeNullableValues<T, bool>(payload, definitionPayload, valueCount, physicalCount,
                column, encoding, definitionLevelEncoding, ref state, bufferPool);
        if (physicalType == typeof(float))
            return TryDecodeNullableValues<T, float>(payload, definitionPayload, valueCount, physicalCount,
                column, encoding, definitionLevelEncoding, ref state, bufferPool);
        if (physicalType == typeof(double))
            return TryDecodeNullableValues<T, double>(payload, definitionPayload, valueCount, physicalCount,
                column, encoding, definitionLevelEncoding, ref state, bufferPool);
        if (physicalType == typeof(byte))
            return TryDecodeNullableValues<T, byte>(payload, definitionPayload, valueCount, physicalCount,
                column, encoding, definitionLevelEncoding, ref state, bufferPool);
        if (physicalType == typeof(ushort))
            return TryDecodeNullableValues<T, ushort>(payload, definitionPayload, valueCount, physicalCount,
                column, encoding, definitionLevelEncoding, ref state, bufferPool);
        if (physicalType == typeof(uint))
            return TryDecodeNullableValues<T, uint>(payload, definitionPayload, valueCount, physicalCount,
                column, encoding, definitionLevelEncoding, ref state, bufferPool);
        if (physicalType == typeof(ulong))
            return TryDecodeNullableValues<T, ulong>(payload, definitionPayload, valueCount, physicalCount,
                column, encoding, definitionLevelEncoding, ref state, bufferPool);
        if (physicalType == typeof(DateOnly))
            return TryDecodeNullableValues<T, DateOnly>(payload, definitionPayload, valueCount, physicalCount,
                column, encoding, definitionLevelEncoding, ref state, bufferPool);
        if (physicalType == typeof(DateTime))
            return TryDecodeNullableValues<T, DateTime>(payload, definitionPayload, valueCount, physicalCount,
                column, encoding, definitionLevelEncoding, ref state, bufferPool);
        if (physicalType == typeof(DateTimeOffset))
            return TryDecodeNullableValues<T, DateTimeOffset>(payload, definitionPayload, valueCount, physicalCount,
                column, encoding, definitionLevelEncoding, ref state, bufferPool);
        if (physicalType == typeof(TimeOnly))
            return TryDecodeNullableValues<T, TimeOnly>(payload, definitionPayload, valueCount, physicalCount,
                column, encoding, definitionLevelEncoding, ref state, bufferPool);
        return false;
    }

    static bool TryDecodeNullableValues<T, TValue>(ReadOnlySpan<byte> payload,
        ReadOnlySpan<byte> definitionPayload, int valueCount, int physicalCount, Column column,
        EncodingKind encoding, EncodingKind definitionLevelEncoding, ref ColumnReadBuffers<T> state,
        IParquetBufferPool bufferPool)
        where TValue : struct
    {
        if (typeof(T) != typeof(TValue?))
            return false;

        var definitionByteLength = checked(valueCount * sizeof(int));
        var physicalOffset = (definitionByteLength + 7) & ~7;
        var physicalByteLength = checked(physicalCount * Unsafe.SizeOf<TValue>());
        var scratch = state.GetScratch(checked(physicalOffset + physicalByteLength), bufferPool);
        var definitions = MemoryMarshal.Cast<byte, int>(scratch[..definitionByteLength]);
        if (definitionPayload.IsEmpty)
            definitions.Fill(1);
        else
            DecodeDefinitionLevels(definitionPayload, valueCount,
                definitionLevelEncoding, definitions, out _);
        var physicalValues = MemoryMarshal.Cast<byte, TValue>(
            scratch.Slice(physicalOffset, physicalByteLength));

        if (state.HasDictionary)
        {
            if (encoding is not (EncodingKind.RleDictionary or EncodingKind.PlainDictionary))
                return false;
            DecodeDictionaryIndexesIntoBuffer(payload, checked((uint)physicalCount),
                state.GetDictionary<TValue>(), physicalValues);
        }
        else if (!TryDecodeValuesIntoNative(payload, column, checked((uint)physicalCount), encoding,
                     physicalValues))
        {
            return false;
        }

        var destination = state.GetValues<T>(valueCount, bufferPool);
        var nullableDestination = Unsafe.As<Span<T>, Span<TValue?>>(ref destination);
        var physicalIndex = 0;
        for (var i = 0; i < definitions.Length; i++)
            nullableDestination[i] = definitions[i] == 0 ? null : physicalValues[physicalIndex++];
        if (physicalIndex != physicalCount)
            throw new CorruptParquetException(
                $"Definition levels consumed {physicalIndex} physical values, expected {physicalCount}.");
        return true;
    }

    internal static bool TryDecodeRequiredPageIntoNative<T>(PageHeader header, ReadOnlySpan<byte> payload,
        Column column, ulong rowCount, ref ColumnReadBuffers<T> state,
        IParquetBufferPool bufferPool, out ColumnBuffer<T> buffer)
    {
        buffer = default;
        if (typeof(T) == typeof(BinaryValueDescriptor) &&
            column.Options.Repetition == ParquetRepetition.Required)
            return TryDecodeBinaryDataPage(header, payload, column, rowCount, ref state, bufferPool,
                optional: false, out buffer);

        if (header.Type is not (PageHeaderType.DataPage or PageHeaderType.DataPageV2) ||
            column.Options.Repetition == ParquetRepetition.Repeated ||
            RuntimeHelpers.IsReferenceOrContainsReferences<T>() || GetPhysicalDecodeType<T>() != typeof(T))
            return false;

        if (header.ValueCount > rowCount)
            throw new CorruptParquetException(
                $"Page value count ({header.ValueCount}) exceeds row group row count ({rowCount}).");

        ReadOnlySpan<byte> dataPayload;
        if (header.Type == PageHeaderType.DataPageV2)
        {
            if (header.NullCount != 0)
                return false;
            var levelBytes = checked((int)(header.RepetitionLevelsByteLength +
                header.DefinitionLevelsByteLength));
            if ((uint)levelBytes > (uint)payload.Length)
                throw new CorruptParquetException(
                    $"Level bytes ({levelBytes}) exceed page payload size ({payload.Length}).");
            dataPayload = payload[levelBytes..];
        }
        else
        {
            if (column.Options.Repetition == ParquetRepetition.Optional)
                return false;
            dataPayload = payload;
        }

        var valueCount = checked((int)header.ValueCount);
        var destination = state.GetValues<T>(valueCount, bufferPool);
        if (state.HasDictionary)
        {
            if (header.Encoding is not (EncodingKind.RleDictionary or EncodingKind.PlainDictionary))
                return false;
            DecodeDictionaryIndexesIntoBuffer(dataPayload, header.ValueCount,
                state.GetDictionary<T>(), destination);
        }
        else if (!TryDecodeValuesIntoNative(dataPayload, column, header.ValueCount, header.Encoding, destination))
        {
            return false;
        }

        buffer = state.CreateNativeBuffer(valueCount);
        return true;
    }

    static bool TryDecodeBinaryDictionaryPage<T>(PageHeader header, ReadOnlySpan<byte> payload,
        Column column, ref ColumnReadBuffers<T> state, IParquetBufferPool bufferPool)
    {
        if (!IsBinaryPhysicalType(column.PhysicalType) || header.Encoding != EncodingKind.Plain)
            return false;

        var valueCount = checked((int)header.ValueCount);
        var lengths = MemoryMarshal.Cast<byte, int>(
            state.GetScratch(checked(valueCount * sizeof(int)), bufferPool));
        var payloadByteLength = ReadPlainBinaryLengths(payload, column, valueCount, lengths);
        var destination = state.GetBinaryDictionary(valueCount, payloadByteLength, bufferPool,
            out var destinationPayload);
        destination.Clear();
        var payloadAddress = valueCount == 0
            ? 0
            : state.Dictionary.DangerousGetAddress() +
              checked(valueCount * Unsafe.SizeOf<BinaryValueDescriptor>());
        FillPlainBinaryValues(payload, column, valueCount, lengths, [], destination,
            destinationPayload, payloadAddress);
        return true;
    }

    static bool TryDecodeBinaryDataPage<T>(PageHeader header, ReadOnlySpan<byte> payload,
        Column column, ulong rowCount, ref ColumnReadBuffers<T> state, IParquetBufferPool bufferPool,
        bool optional, out ColumnBuffer<T> buffer)
    {
        buffer = default;
        if (header.Type is not (PageHeaderType.DataPage or PageHeaderType.DataPageV2) ||
            !IsBinaryPhysicalType(column.PhysicalType) ||
            column.Options.Repetition == ParquetRepetition.Repeated)
            return false;
        if (header.ValueCount > rowCount)
            throw new CorruptParquetException(
                $"Page value count ({header.ValueCount}) exceeds row group row count ({rowCount}).");

        ReadOnlySpan<byte> definitionPayload;
        ReadOnlySpan<byte> dataPayload;
        var expectedPhysicalCount = header.ValueCount;
        if (header.Type == PageHeaderType.DataPageV2)
        {
            if (header.NullCount > header.ValueCount)
                throw new CorruptParquetException(
                    $"Page null count ({header.NullCount}) exceeds value count ({header.ValueCount}).");
            if (!optional && header.NullCount != 0)
                return false;
            var levelBytes = checked((int)(header.RepetitionLevelsByteLength +
                header.DefinitionLevelsByteLength));
            if ((uint)levelBytes > (uint)payload.Length)
                throw new CorruptParquetException(
                    $"Level bytes ({levelBytes}) exceed page payload size ({payload.Length}).");
            definitionPayload = optional && header.DefinitionLevelsByteLength != 0
                ? payload.Slice(checked((int)header.RepetitionLevelsByteLength),
                    checked((int)header.DefinitionLevelsByteLength))
                : [];
            dataPayload = payload[levelBytes..];
            expectedPhysicalCount -= header.NullCount;
        }
        else if (optional)
        {
            var definitionLength = GetDataPageV1LevelPayloadLength(payload, header.ValueCount,
                header.DefinitionLevelEncoding, bitWidth: 1, "definition", out var definitionOffset);
            definitionPayload = payload.Slice(definitionOffset, definitionLength);
            dataPayload = payload[(definitionOffset + definitionLength)..];
        }
        else
        {
            definitionPayload = [];
            dataPayload = payload;
        }

        var valueCount = checked((int)header.ValueCount);
        var scratch = MemoryMarshal.Cast<byte, int>(
            state.GetScratch(checked(valueCount * 3 * sizeof(int)), bufferPool));
        var definitions = scratch[..valueCount];
        var physicalCount = valueCount;
        if (optional && !definitionPayload.IsEmpty)
        {
            DecodeDefinitionLevels(definitionPayload, valueCount, header.DefinitionLevelEncoding,
                definitions, out physicalCount);
            if (header.Type == PageHeaderType.DataPageV2 && physicalCount != expectedPhysicalCount)
                throw new CorruptParquetException(
                    $"Definition levels contain {physicalCount} values, expected {expectedPhysicalCount}.");
        }
        else
        {
            definitions.Fill(1);
            if (optional && expectedPhysicalCount != header.ValueCount)
                throw new CorruptParquetException(
                    $"Page declares {header.NullCount} null values but has no definition levels.");
        }

        if (!TryDecodeBinaryValues(dataPayload, definitions, valueCount, physicalCount, column,
                header.Encoding, scratch[valueCount..], ref state, bufferPool))
            return false;

        buffer = state.CreateNativeBuffer(valueCount);
        return true;
    }

    static bool TryDecodeBinaryValues<T>(ReadOnlySpan<byte> payload, ReadOnlySpan<int> definitions,
        int valueCount, int physicalCount, Column column, EncodingKind encoding, Span<int> scratch,
        ref ColumnReadBuffers<T> state, IParquetBufferPool bufferPool)
    {
        if (state.HasDictionary)
        {
            if (encoding is not (EncodingKind.RleDictionary or EncodingKind.PlainDictionary))
                return false;
            DecodeBinaryDictionaryValues(payload, definitions, valueCount, physicalCount,
                scratch[..physicalCount], ref state, bufferPool);
            return true;
        }

        switch (encoding)
        {
            case EncodingKind.Plain:
                DecodePlainBinaryValues(payload, definitions, valueCount, physicalCount, column,
                    scratch[..physicalCount], ref state, bufferPool);
                return true;
            case EncodingKind.ByteStreamSplit when column.PhysicalType == ParquetPhysicalType.FixedLenByteArray:
                DecodeByteStreamSplitBinaryValues(payload, definitions, valueCount, physicalCount, column,
                    ref state, bufferPool);
                return true;
            case EncodingKind.DeltaLengthByteArray when column.PhysicalType == ParquetPhysicalType.ByteArray:
                DecodeDeltaLengthBinaryValues(payload, definitions, valueCount, physicalCount,
                    scratch[..physicalCount], ref state, bufferPool);
                return true;
            case EncodingKind.DeltaByteArray when column.PhysicalType == ParquetPhysicalType.ByteArray:
                DecodeDeltaBinaryValues(payload, definitions, valueCount, physicalCount,
                    scratch[..checked(physicalCount * 2)], ref state, bufferPool);
                return true;
            default:
                return false;
        }
    }

    static void DecodePlainBinaryValues<T>(ReadOnlySpan<byte> payload, ReadOnlySpan<int> definitions,
        int valueCount, int physicalCount, Column column, Span<int> lengths,
        ref ColumnReadBuffers<T> state, IParquetBufferPool bufferPool)
    {
        var payloadByteLength = ReadPlainBinaryLengths(payload, column, physicalCount, lengths);
        var destination = state.GetBinaryValues(valueCount, payloadByteLength, bufferPool,
            out var destinationPayload);
        destination.Clear();
        var payloadAddress = GetBinaryPayloadAddress(state.Values, valueCount);
        FillPlainBinaryValues(payload, column, physicalCount, lengths, definitions, destination,
            destinationPayload, payloadAddress);
    }

    static void DecodeByteStreamSplitBinaryValues<T>(ReadOnlySpan<byte> payload,
        ReadOnlySpan<int> definitions, int valueCount, int physicalCount, Column column,
        ref ColumnReadBuffers<T> state, IParquetBufferPool bufferPool)
    {
        var valueLength = GetFixedBinaryLength(column);
        var payloadByteLength = GetFixedBinaryPayloadLength(payload, physicalCount, valueLength);
        var destination = state.GetBinaryValues(valueCount, payloadByteLength, bufferPool,
            out var destinationPayload);
        destination.Clear();
        var payloadAddress = GetBinaryPayloadAddress(state.Values, valueCount);
        var logicalIndex = 0;
        var destinationOffset = 0;
        for (var physicalIndex = 0; physicalIndex < physicalCount; physicalIndex++)
        {
            var targetIndex = GetBinaryLogicalIndex(definitions, ref logicalIndex, physicalIndex);
            var valueDestination = destinationPayload.Slice(destinationOffset, valueLength);
            for (var lane = 0; lane < valueLength; lane++)
                valueDestination[lane] = payload[(lane * physicalCount) + physicalIndex];
            destination[targetIndex] = new BinaryValueDescriptor(payloadAddress + destinationOffset, valueLength);
            destinationOffset += valueLength;
        }
    }

    static void DecodeDeltaLengthBinaryValues<T>(ReadOnlySpan<byte> payload,
        ReadOnlySpan<int> definitions, int valueCount, int physicalCount, Span<int> lengths,
        ref ColumnReadBuffers<T> state, IParquetBufferPool bufferPool)
    {
        var consumedLengthBytes = physicalCount == 0
            ? 0
            : DeltaBinaryPackedDecoder.ReadNonNegativeInt32WithConsumedBytes(payload, lengths);
        var remaining = payload[consumedLengthBytes..];
        var payloadByteLength = SumBinaryLengths(lengths, remaining.Length, "Delta length byte array");
        var destination = state.GetBinaryValues(valueCount, payloadByteLength, bufferPool,
            out var destinationPayload);
        destination.Clear();
        var payloadAddress = GetBinaryPayloadAddress(state.Values, valueCount);
        var logicalIndex = 0;
        var destinationOffset = 0;
        for (var physicalIndex = 0; physicalIndex < physicalCount; physicalIndex++)
        {
            var length = lengths[physicalIndex];
            var targetIndex = GetBinaryLogicalIndex(definitions, ref logicalIndex, physicalIndex);
            remaining[..length].CopyTo(destinationPayload[destinationOffset..]);
            destination[targetIndex] = new BinaryValueDescriptor(payloadAddress + destinationOffset, length);
            remaining = remaining[length..];
            destinationOffset += length;
        }
    }

    static void DecodeDeltaBinaryValues<T>(ReadOnlySpan<byte> payload, ReadOnlySpan<int> definitions,
        int valueCount, int physicalCount, Span<int> scratch, ref ColumnReadBuffers<T> state,
        IParquetBufferPool bufferPool)
    {
        var prefixLengths = scratch[..physicalCount];
        var prefixConsumed = physicalCount == 0
            ? 0
            : DeltaBinaryPackedDecoder.ReadNonNegativeInt32WithConsumedBytes(payload, prefixLengths);
        var suffixPayload = payload[prefixConsumed..];
        var suffixLengths = scratch[physicalCount..];
        var suffixConsumed = physicalCount == 0
            ? 0
            : DeltaBinaryPackedDecoder.ReadNonNegativeInt32WithConsumedBytes(suffixPayload, suffixLengths);
        var suffixRemaining = suffixPayload[suffixConsumed..];
        var payloadByteLength = 0;
        var previousLength = 0;
        var remainingSuffixLength = suffixRemaining.Length;
        for (var i = 0; i < physicalCount; i++)
        {
            var prefixLength = prefixLengths[i];
            var suffixLength = suffixLengths[i];
            if (prefixLength > previousLength)
                throw new CorruptParquetException(
                    $"Delta byte array prefix length {prefixLength} exceeds previous value length {previousLength}.");
            if (suffixLength > remainingSuffixLength)
                throw new CorruptParquetException(
                    $"Delta byte array suffix length {suffixLength} exceeds remaining suffix bytes ({remainingSuffixLength}).");
            if (prefixLength > int.MaxValue - suffixLength)
                throw new CorruptParquetException(
                    $"Delta byte array value length overflow (prefix={prefixLength} + suffix={suffixLength}).");
            previousLength = prefixLength + suffixLength;
            payloadByteLength = AddBinaryLength(payloadByteLength, previousLength);
            remainingSuffixLength -= suffixLength;
        }

        var destination = state.GetBinaryValues(valueCount, payloadByteLength, bufferPool,
            out var destinationPayload);
        destination.Clear();
        var payloadAddress = GetBinaryPayloadAddress(state.Values, valueCount);
        var logicalIndex = 0;
        var destinationOffset = 0;
        var previousLogicalIndex = -1;
        for (var physicalIndex = 0; physicalIndex < physicalCount; physicalIndex++)
        {
            var prefixLength = prefixLengths[physicalIndex];
            var suffixLength = suffixLengths[physicalIndex];
            var length = prefixLength + suffixLength;
            var targetIndex = GetBinaryLogicalIndex(definitions, ref logicalIndex, physicalIndex);
            var valueDestination = destinationPayload.Slice(destinationOffset, length);
            if (prefixLength > 0)
                destination[previousLogicalIndex].Span[..prefixLength].CopyTo(valueDestination);
            suffixRemaining[..suffixLength].CopyTo(valueDestination[prefixLength..]);
            suffixRemaining = suffixRemaining[suffixLength..];
            destination[targetIndex] = new BinaryValueDescriptor(payloadAddress + destinationOffset, length);
            previousLogicalIndex = targetIndex;
            destinationOffset += length;
        }
    }

    static void DecodeBinaryDictionaryValues<T>(ReadOnlySpan<byte> payload,
        ReadOnlySpan<int> definitions, int valueCount, int physicalCount, Span<int> indexes,
        ref ColumnReadBuffers<T> state, IParquetBufferPool bufferPool)
    {
        var dictionary = state.GetDictionary<BinaryValueDescriptor>();
        DecodeDictionaryIndexesIntoBuffer(payload, checked((uint)physicalCount), dictionary.Length, indexes);
        var payloadByteLength = 0;
        for (var i = 0; i < indexes.Length; i++)
            payloadByteLength = AddBinaryLength(payloadByteLength, dictionary[indexes[i]].Length);

        var destination = state.GetBinaryValues(valueCount, payloadByteLength, bufferPool,
            out var destinationPayload);
        destination.Clear();
        var payloadAddress = GetBinaryPayloadAddress(state.Values, valueCount);
        var logicalIndex = 0;
        var destinationOffset = 0;
        for (var physicalIndex = 0; physicalIndex < physicalCount; physicalIndex++)
        {
            var value = dictionary[indexes[physicalIndex]];
            var targetIndex = GetBinaryLogicalIndex(definitions, ref logicalIndex, physicalIndex);
            value.Span.CopyTo(destinationPayload[destinationOffset..]);
            destination[targetIndex] = new BinaryValueDescriptor(payloadAddress + destinationOffset, value.Length);
            destinationOffset += value.Length;
        }
    }

    static int ReadPlainBinaryLengths(ReadOnlySpan<byte> payload, Column column, int valueCount,
        Span<int> lengths)
    {
        if (column.PhysicalType == ParquetPhysicalType.ByteArray)
        {
            var remaining = payload;
            var payloadByteLength = 0;
            for (var i = 0; i < valueCount; i++)
            {
                if (remaining.Length < sizeof(int))
                    throw new CorruptParquetException("Payload too short to read byte array length prefix.");
                var unsignedLength = BinaryPrimitives.ReadUInt32LittleEndian(remaining);
                remaining = remaining[sizeof(int)..];
                if (unsignedLength > int.MaxValue)
                    throw new CorruptParquetException(
                        $"Byte array length {unsignedLength} exceeds the supported maximum of {int.MaxValue}.");
                var length = checked((int)unsignedLength);
                if (length > remaining.Length)
                    throw new CorruptParquetException(
                        $"Byte array length {length} exceeds remaining payload ({remaining.Length} bytes).");
                lengths[i] = length;
                payloadByteLength = AddBinaryLength(payloadByteLength, length);
                remaining = remaining[length..];
            }
            return payloadByteLength;
        }

        var valueLength = GetFixedBinaryLength(column);
        var byteLength = GetFixedBinaryPayloadLength(payload, valueCount, valueLength);
        lengths.Fill(valueLength);
        return byteLength;
    }

    static void FillPlainBinaryValues(ReadOnlySpan<byte> payload, Column column, int physicalCount,
        ReadOnlySpan<int> lengths, ReadOnlySpan<int> definitions,
        Span<BinaryValueDescriptor> destination,
        Span<byte> destinationPayload, nint payloadAddress)
    {
        var remaining = payload;
        var logicalIndex = 0;
        var destinationOffset = 0;
        for (var physicalIndex = 0; physicalIndex < physicalCount; physicalIndex++)
        {
            var length = lengths[physicalIndex];
            if (column.PhysicalType == ParquetPhysicalType.ByteArray)
                remaining = remaining[sizeof(int)..];
            var targetIndex = GetBinaryLogicalIndex(definitions, ref logicalIndex, physicalIndex);
            remaining[..length].CopyTo(destinationPayload[destinationOffset..]);
            destination[targetIndex] = new BinaryValueDescriptor(payloadAddress + destinationOffset, length);
            remaining = remaining[length..];
            destinationOffset += length;
        }
    }

    static int GetBinaryLogicalIndex(ReadOnlySpan<int> definitions, ref int logicalIndex,
        int physicalIndex)
    {
        if (definitions.IsEmpty)
            return physicalIndex;
        while (logicalIndex < definitions.Length && definitions[logicalIndex] == 0)
            logicalIndex++;
        if (logicalIndex == definitions.Length)
            throw new CorruptParquetException(
                "Definition levels contain fewer non-null values than the encoded payload.");
        return logicalIndex++;
    }

    static int SumBinaryLengths(ReadOnlySpan<int> lengths, int availablePayloadLength, string encoding)
    {
        var byteLength = 0;
        var remainingLength = availablePayloadLength;
        for (var i = 0; i < lengths.Length; i++)
        {
            var length = lengths[i];
            if (length > remainingLength)
                throw new CorruptParquetException(
                    $"{encoding} entry {i} claims {length} bytes but only {remainingLength} remain.");
            byteLength = AddBinaryLength(byteLength, length);
            remainingLength -= length;
        }
        return byteLength;
    }

    static int AddBinaryLength(int currentLength, int valueLength)
    {
        if (valueLength < 0 || valueLength > int.MaxValue - currentLength)
            throw new CorruptParquetException(
                $"Binary payload length exceeds the supported maximum of {int.MaxValue} bytes.");
        return currentLength + valueLength;
    }

    static int GetFixedBinaryPayloadLength(ReadOnlySpan<byte> payload, int valueCount, int valueLength)
    {
        var byteLength = (long)valueCount * valueLength;
        if (byteLength > int.MaxValue)
            throw new CorruptParquetException(
                $"Fixed-length binary payload exceeds the supported maximum of {int.MaxValue} bytes.");
        if (byteLength > payload.Length)
            throw new CorruptParquetException(
                $"Payload ({payload.Length} bytes) is too short to decode {valueCount} fixed-length values of {valueLength} bytes each.");
        return checked((int)byteLength);
    }

    static int GetFixedBinaryLength(Column column)
        => column.PhysicalType switch
        {
            ParquetPhysicalType.FixedLenByteArray when column.Options.TypeLength is > 0 and <= int.MaxValue
                => checked((int)column.Options.TypeLength),
            ParquetPhysicalType.Int96 => 12,
            ParquetPhysicalType.FixedLenByteArray => throw new CorruptParquetException(
                $"Column '{column.Name}' has invalid fixed length {column.Options.TypeLength}."),
            _ => throw new CorruptParquetException(
                $"Column '{column.Name}' is not a fixed-length binary column.")
        };

    static nint GetBinaryPayloadAddress(ParquetBuffer buffer, int valueCount)
        => valueCount == 0
            ? 0
            : buffer.DangerousGetAddress() +
              checked(valueCount * Unsafe.SizeOf<BinaryValueDescriptor>());

    static bool IsBinaryPhysicalType(ParquetPhysicalType physicalType)
        => physicalType is ParquetPhysicalType.ByteArray
            or ParquetPhysicalType.FixedLenByteArray
            or ParquetPhysicalType.Int96;

    static bool TryDecodeValuesIntoNative<T>(ReadOnlySpan<byte> payload, Column column, uint valueCount,
        EncodingKind encoding, Span<T> destination)
    {
        switch (encoding)
        {
            case EncodingKind.Plain:
                return TryDecodePlainIntoNative(payload, column, valueCount, destination);
            case EncodingKind.Rle when typeof(T) == typeof(bool):
            {
                var typed = Unsafe.As<Span<T>, Span<bool>>(ref destination);
                DecodeBooleanRle(payload, typed);
                return true;
            }
            case EncodingKind.ByteStreamSplit:
                return TryDecodeByteStreamSplitIntoNative(payload, column, valueCount, destination);
            case EncodingKind.DeltaBinaryPacked:
                return TryDecodeDeltaBinaryPackedIntoNative(payload, column, destination);
            default:
                return false;
        }
    }

    static bool TryDecodePlainIntoNative<T>(ReadOnlySpan<byte> payload, Column column, uint valueCount,
        Span<T> destination)
    {
        if (typeof(T) == typeof(bool) && column.PhysicalType == ParquetPhysicalType.Boolean)
        {
            if ((uint)payload.Length < (valueCount + 7u) / 8u)
                throw new CorruptParquetException(
                    $"Payload ({payload.Length} bytes) is too short to decode {valueCount} plain boolean values.");
            var typed = Unsafe.As<Span<T>, Span<bool>>(ref destination);
            for (var i = 0; i < typed.Length; i++)
                typed[i] = ((payload[i >> 3] >> (i & 7)) & 1) != 0;
            return true;
        }

        if (typeof(T) == typeof(int) && column.PhysicalType == ParquetPhysicalType.Int32)
        {
            ValidatePlainPayload(payload, valueCount, sizeof(int));
            CopyLittleEndianInt32(payload, Unsafe.As<Span<T>, Span<int>>(ref destination));
            return true;
        }
        if (typeof(T) == typeof(byte) && column.PhysicalType == ParquetPhysicalType.Int32)
        {
            ValidatePlainPayload(payload, valueCount, sizeof(int));
            var typed = Unsafe.As<Span<T>, Span<byte>>(ref destination);
            for (var i = 0; i < typed.Length; i++)
                typed[i] = unchecked((byte)BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(i * 4, 4)));
            return true;
        }
        if (typeof(T) == typeof(ushort) && column.PhysicalType == ParquetPhysicalType.Int32)
        {
            ValidatePlainPayload(payload, valueCount, sizeof(int));
            var typed = Unsafe.As<Span<T>, Span<ushort>>(ref destination);
            for (var i = 0; i < typed.Length; i++)
                typed[i] = unchecked((ushort)BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(i * 4, 4)));
            return true;
        }
        if (typeof(T) == typeof(uint) && column.PhysicalType == ParquetPhysicalType.Int32)
        {
            ValidatePlainPayload(payload, valueCount, sizeof(uint));
            CopyLittleEndianUInt32(payload, Unsafe.As<Span<T>, Span<uint>>(ref destination));
            return true;
        }
        if (typeof(T) == typeof(long) && column.PhysicalType == ParquetPhysicalType.Int64)
        {
            ValidatePlainPayload(payload, valueCount, sizeof(long));
            CopyLittleEndianInt64(payload, Unsafe.As<Span<T>, Span<long>>(ref destination));
            return true;
        }
        if (typeof(T) == typeof(ulong) && column.PhysicalType == ParquetPhysicalType.Int64)
        {
            ValidatePlainPayload(payload, valueCount, sizeof(ulong));
            CopyLittleEndianUInt64(payload, Unsafe.As<Span<T>, Span<ulong>>(ref destination));
            return true;
        }
        if (typeof(T) == typeof(float) && column.PhysicalType == ParquetPhysicalType.Float)
        {
            ValidatePlainPayload(payload, valueCount, sizeof(float));
            CopyLittleEndianFloat(payload, Unsafe.As<Span<T>, Span<float>>(ref destination));
            return true;
        }
        if (typeof(T) == typeof(double) && column.PhysicalType == ParquetPhysicalType.Double)
        {
            ValidatePlainPayload(payload, valueCount, sizeof(double));
            CopyLittleEndianDouble(payload, Unsafe.As<Span<T>, Span<double>>(ref destination));
            return true;
        }
        if (typeof(T) == typeof(DateOnly) && column.PhysicalType == ParquetPhysicalType.Int32)
        {
            ValidatePlainPayload(payload, valueCount, sizeof(int));
            var typed = Unsafe.As<Span<T>, Span<DateOnly>>(ref destination);
            for (var i = 0; i < typed.Length; i++)
                typed[i] = DecodeDate(BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(i * 4, 4)));
            return true;
        }
        if (typeof(T) == typeof(TimeOnly) && column.PhysicalType == ParquetPhysicalType.Int64)
        {
            ValidatePlainPayload(payload, valueCount, sizeof(long));
            var typed = Unsafe.As<Span<T>, Span<TimeOnly>>(ref destination);
            for (var i = 0; i < typed.Length; i++)
                typed[i] = DecodeTime(BinaryPrimitives.ReadInt64LittleEndian(payload.Slice(i * 8, 8)),
                    column.LogicalType);
            return true;
        }
        if (typeof(T) == typeof(DateTimeOffset) && column.PhysicalType == ParquetPhysicalType.Int64)
        {
            ValidatePlainPayload(payload, valueCount, sizeof(long));
            var typed = Unsafe.As<Span<T>, Span<DateTimeOffset>>(ref destination);
            for (var i = 0; i < typed.Length; i++)
            {
                var raw = BinaryPrimitives.ReadInt64LittleEndian(payload.Slice(i * 8, 8));
                typed[i] = DecodeTimestamp(raw, column.LogicalType);
            }
            return true;
        }
        if (typeof(T) == typeof(DateTime) && column.PhysicalType == ParquetPhysicalType.Int64)
        {
            ValidatePlainPayload(payload, valueCount, sizeof(long));
            var typed = Unsafe.As<Span<T>, Span<DateTime>>(ref destination);
            for (var i = 0; i < typed.Length; i++)
            {
                var raw = BinaryPrimitives.ReadInt64LittleEndian(payload.Slice(i * 8, 8));
                typed[i] = DecodeDateTime(raw, column.LogicalType);
            }
            return true;
        }

        return false;
    }

    static bool TryDecodeByteStreamSplitIntoNative<T>(ReadOnlySpan<byte> payload, Column column,
        uint valueCount, Span<T> destination)
    {
        switch (column.PhysicalType)
        {
            case ParquetPhysicalType.Int32 when typeof(T) == typeof(int):
                DecodeByteStreamSplitInt32(payload, Unsafe.As<Span<T>, Span<int>>(ref destination));
                return true;
            case ParquetPhysicalType.Int64 when typeof(T) == typeof(long):
                DecodeByteStreamSplitInt64(payload, Unsafe.As<Span<T>, Span<long>>(ref destination));
                return true;
            case ParquetPhysicalType.Int64 when typeof(T) == typeof(ulong):
                DecodeByteStreamSplitUInt64(payload, Unsafe.As<Span<T>, Span<ulong>>(ref destination));
                return true;
            case ParquetPhysicalType.Int32 when typeof(T) == typeof(DateOnly):
            {
                var raw = Unsafe.As<Span<T>, Span<int>>(ref destination);
                DecodeByteStreamSplitInt32(payload, raw);
                var typed = Unsafe.As<Span<T>, Span<DateOnly>>(ref destination);
                for (var i = 0; i < typed.Length; i++)
                    typed[i] = DecodeDate(raw[i]);
                return true;
            }
            case ParquetPhysicalType.Int64 when typeof(T) == typeof(TimeOnly):
            {
                var raw = Unsafe.As<Span<T>, Span<long>>(ref destination);
                DecodeByteStreamSplitInt64(payload, raw);
                var typed = Unsafe.As<Span<T>, Span<TimeOnly>>(ref destination);
                for (var i = 0; i < typed.Length; i++)
                    typed[i] = DecodeTime(raw[i], column.LogicalType);
                return true;
            }
            case ParquetPhysicalType.Int64 when typeof(T) == typeof(DateTime):
            {
                var raw = Unsafe.As<Span<T>, Span<long>>(ref destination);
                DecodeByteStreamSplitInt64(payload, raw);
                var typed = Unsafe.As<Span<T>, Span<DateTime>>(ref destination);
                for (var i = 0; i < typed.Length; i++)
                    typed[i] = DecodeDateTime(raw[i], column.LogicalType);
                return true;
            }
            case ParquetPhysicalType.Int64 when typeof(T) == typeof(DateTimeOffset):
            {
                var raw = new long[destination.Length];
                DecodeByteStreamSplitInt64(payload, raw);
                var typed = Unsafe.As<Span<T>, Span<DateTimeOffset>>(ref destination);
                for (var i = 0; i < typed.Length; i++)
                    typed[i] = DecodeTimestamp(raw[i], column.LogicalType);
                return true;
            }
            case ParquetPhysicalType.Float when typeof(T) == typeof(float):
                DecodeByteStreamSplitFloat(payload, Unsafe.As<Span<T>, Span<float>>(ref destination));
                return true;
            case ParquetPhysicalType.Double when typeof(T) == typeof(double):
                DecodeByteStreamSplitDouble(payload, Unsafe.As<Span<T>, Span<double>>(ref destination));
                return true;
            case ParquetPhysicalType.Int32 when typeof(T) == typeof(byte):
            {
                var typed = Unsafe.As<Span<T>, Span<byte>>(ref destination);
                for (var i = 0; i < typed.Length; i++)
                    typed[i] = (byte)(payload[i] | (payload[(int)valueCount + i] << 8) |
                        (payload[((int)valueCount * 2) + i] << 16) |
                        (payload[((int)valueCount * 3) + i] << 24));
                return true;
            }
            case ParquetPhysicalType.Int32 when typeof(T) == typeof(ushort):
            {
                var typed = Unsafe.As<Span<T>, Span<ushort>>(ref destination);
                for (var i = 0; i < typed.Length; i++)
                    typed[i] = unchecked((ushort)(payload[i] | (payload[(int)valueCount + i] << 8) |
                        (payload[((int)valueCount * 2) + i] << 16) |
                        (payload[((int)valueCount * 3) + i] << 24)));
                return true;
            }
            case ParquetPhysicalType.Int32 when typeof(T) == typeof(uint):
            {
                var typed = Unsafe.As<Span<T>, Span<uint>>(ref destination);
                for (var i = 0; i < typed.Length; i++)
                    typed[i] = unchecked((uint)(payload[i] | (payload[(int)valueCount + i] << 8) |
                        (payload[((int)valueCount * 2) + i] << 16) |
                        (payload[((int)valueCount * 3) + i] << 24)));
                return true;
            }
            default:
                return false;
        }
    }

    static bool TryDecodeDeltaBinaryPackedIntoNative<T>(ReadOnlySpan<byte> payload, Column column,
        Span<T> destination)
    {
        if (column.PhysicalType == ParquetPhysicalType.Int32 && typeof(T) == typeof(int))
        {
            DeltaBinaryPackedDecoder.ReadInt32(payload, Unsafe.As<Span<T>, Span<int>>(ref destination));
            return true;
        }
        if (column.PhysicalType == ParquetPhysicalType.Int32 && typeof(T) == typeof(byte))
        {
            DeltaBinaryPackedDecoder.ReadNarrowInt32(payload,
                Unsafe.As<Span<T>, Span<byte>>(ref destination));
            return true;
        }
        if (column.PhysicalType == ParquetPhysicalType.Int32 && typeof(T) == typeof(ushort))
        {
            DeltaBinaryPackedDecoder.ReadNarrowInt32(payload,
                Unsafe.As<Span<T>, Span<ushort>>(ref destination));
            return true;
        }
        if (column.PhysicalType == ParquetPhysicalType.Int32 && typeof(T) == typeof(uint))
        {
            DeltaBinaryPackedDecoder.ReadInt32(payload,
                Unsafe.As<Span<T>, Span<int>>(ref destination));
            return true;
        }
        if (column.PhysicalType == ParquetPhysicalType.Int64 && typeof(T) == typeof(long))
        {
            DeltaBinaryPackedDecoder.ReadInt64(payload, Unsafe.As<Span<T>, Span<long>>(ref destination));
            return true;
        }
        if (column.PhysicalType == ParquetPhysicalType.Int64 && typeof(T) == typeof(ulong))
        {
            DeltaBinaryPackedDecoder.ReadInt64(payload,
                Unsafe.As<Span<T>, Span<long>>(ref destination));
            return true;
        }
        if (column.PhysicalType == ParquetPhysicalType.Int32 && typeof(T) == typeof(DateOnly))
        {
            var raw = Unsafe.As<Span<T>, Span<int>>(ref destination);
            DeltaBinaryPackedDecoder.ReadInt32(payload, raw);
            var typed = Unsafe.As<Span<T>, Span<DateOnly>>(ref destination);
            for (var i = 0; i < typed.Length; i++)
                typed[i] = DecodeDate(raw[i]);
            return true;
        }
        if (column.PhysicalType == ParquetPhysicalType.Int64 && typeof(T) == typeof(TimeOnly))
        {
            var raw = Unsafe.As<Span<T>, Span<long>>(ref destination);
            DeltaBinaryPackedDecoder.ReadInt64(payload, raw);
            var typed = Unsafe.As<Span<T>, Span<TimeOnly>>(ref destination);
            for (var i = 0; i < typed.Length; i++)
                typed[i] = DecodeTime(raw[i], column.LogicalType);
            return true;
        }
        if (column.PhysicalType == ParquetPhysicalType.Int64 && typeof(T) == typeof(DateTime))
        {
            var raw = Unsafe.As<Span<T>, Span<long>>(ref destination);
            DeltaBinaryPackedDecoder.ReadInt64(payload, raw);
            var typed = Unsafe.As<Span<T>, Span<DateTime>>(ref destination);
            for (var i = 0; i < typed.Length; i++)
                typed[i] = DecodeDateTime(raw[i], column.LogicalType);
            return true;
        }
        if (column.PhysicalType == ParquetPhysicalType.Int64 && typeof(T) == typeof(DateTimeOffset))
        {
            var raw = new long[destination.Length];
            DeltaBinaryPackedDecoder.ReadInt64(payload, raw);
            var typed = Unsafe.As<Span<T>, Span<DateTimeOffset>>(ref destination);
            for (var i = 0; i < typed.Length; i++)
                typed[i] = DecodeTimestamp(raw[i], column.LogicalType);
            return true;
        }
        return false;
    }

    static DateOnly DecodeDate(int days)
        => DateOnly.FromDayNumber(checked(UnixEpochDate.DayNumber + days));

    static TimeOnly DecodeTime(long raw, LogicalType? logicalType)
        => logicalType switch
        {
            LogicalType.Time { Unit: TimeUnit.Millis } => new TimeOnly(checked(raw * TimeSpan.TicksPerMillisecond)),
            LogicalType.Time { Unit: TimeUnit.Micros } => new TimeOnly(checked(raw * 10)),
            LogicalType.Time { Unit: TimeUnit.Nanos } => new TimeOnly(raw / 100),
            _ => throw new CorruptParquetException("TimeOnly projection requires a time logical type.")
        };

    static DateTimeOffset DecodeTimestamp(long raw, LogicalType? logicalType)
    {
        if (logicalType is LogicalType.Timestamp { IsAdjustedToUtc: false })
            throw new NotSupportedException(
                "DateTimeOffset projection is not supported for timestamps with local semantics.");

        return DecodeTimestampValue(raw, logicalType);
    }

    static DateTimeOffset DecodeTimestampValue(long raw, LogicalType? logicalType)
        => logicalType switch
        {
            LogicalType.Timestamp { Unit: TimeUnit.Millis } => DateTimeOffset.FromUnixTimeMilliseconds(raw),
            LogicalType.Timestamp { Unit: TimeUnit.Micros } => DateTimeOffset.UnixEpoch.AddTicks(checked(raw * 10)),
            LogicalType.Timestamp { Unit: TimeUnit.Nanos } => DateTimeOffset.UnixEpoch.AddTicks(raw / 100),
            _ => throw new CorruptParquetException("Timestamp projection requires a timestamp logical type.")
        };

    static DateTime DecodeDateTime(long raw, LogicalType? logicalType)
    {
        var value = DecodeTimestampValue(raw, logicalType).UtcDateTime;
        return logicalType is LogicalType.Timestamp { IsAdjustedToUtc: false }
            ? DateTime.SpecifyKind(value, DateTimeKind.Unspecified)
            : value;
    }

    internal static bool TryDecodePage<T>(PageHeader header, ReadOnlySpan<byte> payload, Column column,
        InternalColumnChunkMetadata columnChunk, ulong rowCount, ref Array? dictionary, ref T[]? dictionaryBuffer,
        ref T[]? valuesBuffer, IParquetBufferPool bufferPool, ref ParquetBuffer scratchBuffer,
        out ReadOnlyMemory<T> values, out EncodingKind encoding)
    {
        ArgumentNullException.ThrowIfNull(column);

        if (column.Options.Repetition == ParquetRepetition.Repeated)
            throw new NotSupportedException($"Repeated readback is not implemented yet for column '{column.Name}'.");

        switch (header.Type)
        {
            case PageHeaderType.DictionaryPage:
                dictionary = DecodeDictionaryPage(payload, column, header, CompressionKind.None,
                    ref dictionaryBuffer, GetPhysicalDecodeType<T>(), bufferPool);
                values = ReadOnlyMemory<T>.Empty;
                encoding = default;
                return false;
            case PageHeaderType.DataPage:
            {
                ParquetBuffer decompressionBuffer = default;
                try
                {
                    values = DecodeDataPageV1(payload, column, header, CompressionKind.None, rowCount, dictionary,
                        ref valuesBuffer, bufferPool, ref scratchBuffer,
                        ref decompressionBuffer);
                    encoding = header.Encoding;
                    return true;
                }
                finally
                {
                    decompressionBuffer.Dispose();
                }
            }
            case PageHeaderType.DataPageV2:
                values = DecodeDataPageV2Payload(payload, column, header, columnChunk, rowCount, dictionary,
                    ref valuesBuffer, bufferPool, ref scratchBuffer);
                encoding = header.Encoding;
                return true;
            default:
                throw new NotSupportedException($"Page type '{header.Type}' is not supported.");
        }
    }

    static ReadOnlyMemory<T> DecodeDataPageV2Payload<T>(ReadOnlySpan<byte> payload, Column column,
        PageHeader header, InternalColumnChunkMetadata columnChunk, ulong rowCount, Array? dictionary,
        ref T[]? valuesBuffer, IParquetBufferPool bufferPool, ref ParquetBuffer scratchBuffer)
    {
        var repLen = header.RepetitionLevelsByteLength;
        var defLen = header.DefinitionLevelsByteLength;
        var levelBytes = repLen + defLen;
        if (header.NullCount > header.ValueCount)
            throw new CorruptParquetException(
                $"Page null count ({header.NullCount}) exceeds value count ({header.ValueCount}).");
        if (header.ValueCount > rowCount)
            throw new CorruptParquetException(
                $"Page value count ({header.ValueCount}) exceeds row group row count ({rowCount}).");
        if (levelBytes > (uint)payload.Length)
            throw new CorruptParquetException(
                $"Level bytes ({levelBytes}) exceed page payload size ({payload.Length}).");
        if (payload.Length > 0 && columnChunk.Compression != CompressionKind.None &&
            header.IsCompressed && payload.Length != header.UncompressedPageSize)
            throw new CorruptParquetException("Physical page cursor returned a compressed DataPageV2 payload.");

        var totalValueCount = header.ValueCount;
        var physicalValueCount = header.ValueCount - header.NullCount;
        var definitionPayload = defLen > 0 ? payload.Slice((int)repLen, (int)defLen) : default;
        var dataPayload = payload[(int)levelBytes..];
        var physicalDecodeType = GetPhysicalDecodeType<T>();
        var isNullableValueType = physicalDecodeType != typeof(T);
        var needsNullExpansion = isNullableValueType
            || (!typeof(T).IsValueType && header.NullCount > 0 && defLen > 0);

        if (dictionary is null)
        {
            if (needsNullExpansion)
                return DecodeValuesWithNullExpansion<T>(dataPayload, definitionPayload, column, totalValueCount,
                    physicalValueCount, header.Encoding, EncodingKind.Rle, header.NullCount > 0);
            if (header.NullCount > 0 && defLen > 0)
                return (T[])DecodeValues(dataPayload, column, physicalValueCount, header.Encoding, typeof(T));
            if (TryDecodeValuesIntoBuffer(dataPayload, column, physicalValueCount, header.Encoding, ref valuesBuffer,
                    bufferPool, ref scratchBuffer, out var values))
                return values;
            return (T[])DecodeValues(dataPayload, column, physicalValueCount, header.Encoding, typeof(T));
        }

        if (header.NullCount > 0 && defLen > 0)
            return DecodeDictionaryIndexesWithNulls<T>(dataPayload, totalValueCount, physicalValueCount, dictionary,
                definitionPayload, EncodingKind.Rle);

        DecodeDictionaryIndexes(dataPayload, physicalValueCount, dictionary, ref valuesBuffer, out var decoded);
        return decoded;
    }

    internal static bool TryReadNextDataPage<T>(byte[] buffer, int bufferLength, ref int offset, Column column,
        InternalColumnChunkMetadata columnChunk, ulong rowCount, ref Array? dictionary, ref T[]? dictionaryBuffer,
        ref T[]? valuesBuffer, IParquetBufferPool bufferPool, ref ParquetBuffer scratchBuffer,
        ref ParquetBuffer decompressionBuffer, out ReadOnlyMemory<T> values,
        out EncodingKind encoding)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(column);

        if (column.Options.Repetition == ParquetRepetition.Repeated)
            throw new NotSupportedException($"Repeated readback is not implemented yet for column '{column.Name}'.");

        while (offset < bufferLength)
        {
            var maxUncompressedPageSize = (uint)Math.Min(columnChunk.TotalUncompressedSize, uint.MaxValue);
            var header = PageHeaderReader.Read(buffer.AsSpan(offset, bufferLength - offset), maxUncompressedPageSize);
            offset += header.HeaderLength;

            if (header.CompressedPageSize > (uint)(bufferLength - offset))
                throw new CorruptParquetException(
                    $"Page compressed size ({header.CompressedPageSize}) exceeds remaining column chunk buffer ({bufferLength - offset}).");

            var compressedPageSize = checked((int)header.CompressedPageSize);
            var payload = buffer.AsSpan(offset, compressedPageSize);
            offset += compressedPageSize;

            switch (header.Type)
            {
                case PageHeaderType.DictionaryPage:
                    dictionary = DecodeDictionaryPage(payload, column, header, columnChunk.Compression,
                        ref dictionaryBuffer, GetPhysicalDecodeType<T>(), bufferPool);
                    break;
                case PageHeaderType.DataPage:
                {
                    values = DecodeDataPageV1(payload, column, header, columnChunk.Compression, rowCount, dictionary,
                        ref valuesBuffer, bufferPool, ref scratchBuffer,
                        ref decompressionBuffer);
                    encoding = header.Encoding;
                    return true;
                }
                case PageHeaderType.DataPageV2:
                {
                    var repLen = header.RepetitionLevelsByteLength;
                    var defLen = header.DefinitionLevelsByteLength;
                    var levelBytes = repLen + defLen;
                    if (header.NullCount > header.ValueCount)
                        throw new CorruptParquetException(
                            $"Page null count ({header.NullCount}) exceeds value count ({header.ValueCount}).");
                    if (header.ValueCount > rowCount)
                        throw new CorruptParquetException(
                            $"Page value count ({header.ValueCount}) exceeds row group row count ({rowCount}).");
                    var totalValueCount = header.ValueCount;
                    var physicalValueCount = header.ValueCount - header.NullCount;

                    // In DataPageV2, levels are always uncompressed; only the values portion may be compressed.
                    if (levelBytes > (uint)payload.Length)
                        throw new CorruptParquetException(
                            $"Level bytes ({levelBytes}) exceed compressed page size ({payload.Length}).");
                    var definitionPayload = defLen > 0 ? payload.Slice((int)repLen, (int)defLen) : default;
                    var dataPayload = payload[(int)levelBytes..];

                    ReadOnlySpan<byte> effectiveData;
                    if (header.IsCompressed && dataPayload.Length > 0)
                    {
                        if (levelBytes > header.UncompressedPageSize)
                            throw new CorruptParquetException(
                                $"Level bytes ({levelBytes}) exceed uncompressed page size ({header.UncompressedPageSize}).");
                        var expectedUncompressedDataSize = header.UncompressedPageSize - levelBytes;
                        EnsureByteBuffer(ref decompressionBuffer, (int)expectedUncompressedDataSize, bufferPool);
                        var decompBuf = decompressionBuffer.Span[..(int)expectedUncompressedDataSize];
                        ParquetDecompressor.DecompressInto(dataPayload, columnChunk.Compression, decompBuf);
                        effectiveData = decompressionBuffer.Span[..(int)expectedUncompressedDataSize];
                    }
                    else
                    {
                        effectiveData = dataPayload;
                    }

                    var physicalDecodeType = GetPhysicalDecodeType<T>();
                    // Nullable value types (int?, long?, …) always need expansion since the physical type differs.
                    // Reference types such as byte[] need expansion when there are actual nulls.
                    var isNullableValueType = physicalDecodeType != typeof(T);
                    var needsNullExpansion = isNullableValueType
                        || (!typeof(T).IsValueType && header.NullCount > 0 && defLen > 0);

                    if (dictionary is null)
                    {
                        if (needsNullExpansion)
                        {
                            values = DecodeValuesWithNullExpansion<T>(effectiveData, definitionPayload, column,
                                totalValueCount, physicalValueCount, header.Encoding, EncodingKind.Rle,
                                header.NullCount > 0);
                        }
                        else if (header.NullCount > 0 && defLen > 0)
                        {
                            // Non-nullable value type with actual nulls — decode physical values only.
                            values = (T[])DecodeValues(effectiveData, column, physicalValueCount, header.Encoding, typeof(T));
                        }
                        else if (TryDecodeValuesIntoBuffer(effectiveData, column, physicalValueCount, header.Encoding,
                                     ref valuesBuffer, bufferPool, ref scratchBuffer, out values))
                        {
                            encoding = header.Encoding;
                            return true;
                        }
                        else
                        {
                            values = (T[])DecodeValues(effectiveData, column, physicalValueCount, header.Encoding, typeof(T));
                        }
                    }
                    else
                    {
                        if (header.NullCount > 0 && defLen > 0)
                        {
                            values = DecodeDictionaryIndexesWithNulls<T>(effectiveData, totalValueCount, physicalValueCount,
                                dictionary, definitionPayload, EncodingKind.Rle);
                        }
                        else
                        {
                            DecodeDictionaryIndexes(effectiveData, physicalValueCount, dictionary, ref valuesBuffer,
                                out values);
                        }
                    }

                    encoding = header.Encoding;
                    return true;
                }
                default:
                    throw new NotSupportedException($"Page type '{header.Type}' is not supported.");
            }
        }

        values = ReadOnlyMemory<T>.Empty;
        encoding = default;
        return false;
    }

    static ReadOnlyMemory<T> DecodeDataPageV1<T>(ReadOnlySpan<byte> payload, Column column, PageHeader header,
        CompressionKind compression, ulong rowCount, Array? dictionary, ref T[]? valuesBuffer,
        IParquetBufferPool bufferPool, ref ParquetBuffer scratchBuffer,
        ref ParquetBuffer decompressionBuffer)
    {
        if (header.ValueCount > rowCount)
            throw new CorruptParquetException(
                $"Page value count ({header.ValueCount}) exceeds row group row count ({rowCount}).");

        ReadOnlySpan<byte> remaining;
        if (compression != CompressionKind.None && payload.Length > 0)
        {
            EnsureByteBuffer(ref decompressionBuffer, (int)header.UncompressedPageSize, bufferPool);
            var decompBuf = decompressionBuffer.Span[..(int)header.UncompressedPageSize];
            ParquetDecompressor.DecompressInto(payload, compression, decompBuf);
            remaining = decompressionBuffer.Span[..(int)header.UncompressedPageSize];
        }
        else
        {
            remaining = payload;
        }

        var hasDefinitionLevels = column.Options.Repetition == ParquetRepetition.Optional;
        var definitionPayload = ReadOnlySpan<byte>.Empty;
        var physicalValueCount = header.ValueCount;
        var nullCount = 0U;
        if (hasDefinitionLevels)
        {
            var definitionLength = GetDataPageV1LevelPayloadLength(remaining, header.ValueCount,
                header.DefinitionLevelEncoding, bitWidth: 1, "definition", out var definitionOffset);
            definitionPayload = remaining.Slice(definitionOffset, definitionLength);
            remaining = remaining[(definitionOffset + definitionLength)..];
            _ = ReadDefinitionLevels(definitionPayload, header.ValueCount, header.DefinitionLevelEncoding,
                out var nonNullCount);
            physicalValueCount = checked((uint)nonNullCount);
            nullCount = header.ValueCount - physicalValueCount;
        }

        var physicalDecodeType = GetPhysicalDecodeType<T>();
        var isNullableValueType = physicalDecodeType != typeof(T);
        var needsNullExpansion = isNullableValueType
            || (!typeof(T).IsValueType && nullCount > 0 && hasDefinitionLevels);

        if (dictionary is null)
        {
            if (needsNullExpansion)
                return DecodeValuesWithNullExpansion<T>(remaining, definitionPayload, column, header.ValueCount,
                    physicalValueCount, header.Encoding, header.DefinitionLevelEncoding, nullCount > 0);
            if (nullCount > 0 && hasDefinitionLevels)
                return (T[])DecodeValues(remaining, column, physicalValueCount, header.Encoding, typeof(T));
            if (TryDecodeValuesIntoBuffer(remaining, column, physicalValueCount, header.Encoding, ref valuesBuffer,
                    bufferPool, ref scratchBuffer, out var values))
                return values;
            return (T[])DecodeValues(remaining, column, physicalValueCount, header.Encoding, typeof(T));
        }

        if (nullCount > 0 && hasDefinitionLevels)
            return DecodeDictionaryIndexesWithNulls<T>(remaining, header.ValueCount, physicalValueCount, dictionary,
                definitionPayload, header.DefinitionLevelEncoding);

        DecodeDictionaryIndexes(remaining, physicalValueCount, dictionary, ref valuesBuffer, out var decoded);
        return decoded;
    }

    static int ReadLengthPrefixedLevelPayloadLength(ReadOnlySpan<byte> payload, string levelName)
    {
        if (payload.Length < sizeof(int))
            throw new CorruptParquetException($"DataPageV1 {levelName} levels are missing their length prefix.");

        var length = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        var remainingLength = payload.Length - sizeof(int);
        if (length > (uint)remainingLength)
            throw new CorruptParquetException(
                $"DataPageV1 {levelName} levels claim {length} bytes but only {remainingLength} remain.");

        return (int)length;
    }

    static int GetDataPageV1LevelPayloadLength(ReadOnlySpan<byte> payload, uint valueCount,
        EncodingKind encoding, int bitWidth, string levelName, out int payloadOffset)
    {
        int byteLength;
        switch (encoding)
        {
            case EncodingKind.Rle:
                byteLength = ReadLengthPrefixedLevelPayloadLength(payload, levelName);
                payloadOffset = sizeof(int);
                break;
            case EncodingKind.BitPacked:
                byteLength = LegacyBitPackedDecoder.GetByteCount(checked((int)valueCount), bitWidth);
                payloadOffset = 0;
                break;
            default:
                throw new NotSupportedException(
                    $"DataPageV1 {levelName} level encoding '{encoding}' is not supported.");
        }

        var totalLength = checked(payloadOffset + byteLength);
        if (totalLength > payload.Length)
            throw new CorruptParquetException(
                $"DataPageV1 {levelName} levels require {totalLength} bytes but only {payload.Length} remain.");
        return byteLength;
    }

    static bool TryDecodeValuesIntoBuffer<T>(ReadOnlySpan<byte> payload, Column column, uint valueCount,
        EncodingKind encoding, ref T[]? valuesBuffer, IParquetBufferPool bufferPool,
        ref ParquetBuffer scratchBuffer, out ReadOnlyMemory<T> values)
    {
        switch (encoding)
        {
            case EncodingKind.Plain:
                return TryDecodePlainIntoBuffer(payload, column, valueCount, ref valuesBuffer, out values);
            case EncodingKind.Rle:
                return TryDecodeBooleanRleIntoBuffer(payload, valueCount, ref valuesBuffer, out values);
            case EncodingKind.ByteStreamSplit:
                return TryDecodeByteStreamSplitIntoBuffer(payload, column, valueCount, ref valuesBuffer, out values);
            case EncodingKind.DeltaBinaryPacked:
                return TryDecodeDeltaBinaryPackedIntoBuffer(payload, column, valueCount, ref valuesBuffer, out values);
            case EncodingKind.DeltaLengthByteArray:
                return TryDecodeDeltaLengthByteArrayIntoBuffer(payload, column, valueCount, ref valuesBuffer, bufferPool,
                    ref scratchBuffer, out values);
            case EncodingKind.DeltaByteArray:
                return TryDecodeDeltaByteArrayIntoBuffer(payload, column, valueCount, ref valuesBuffer, bufferPool,
                    ref scratchBuffer, out values);
            default:
                values = default;
                return false;
        }
    }

    static void ValidatePlainPayload(ReadOnlySpan<byte> payload, uint valueCount, uint elementSize)
    {
        if (valueCount > (uint)payload.Length / elementSize)
            throw new CorruptParquetException(
                $"Payload ({payload.Length} bytes) is too short to decode {valueCount} plain values of {elementSize} bytes each.");
    }

    static bool TryDecodePlainIntoBuffer<T>(ReadOnlySpan<byte> payload, Column column, uint valueCount,
        ref T[]? valuesBuffer, out ReadOnlyMemory<T> values)
    {
        if (typeof(T) == typeof(bool) && column.PhysicalType == ParquetPhysicalType.Boolean)
        {
            if ((uint)payload.Length < (valueCount + 7u) / 8u)
                throw new CorruptParquetException(
                    $"Payload ({payload.Length} bytes) is too short to decode {valueCount} plain boolean values.");
            var typed = (bool[])(object)EnsureManagedBuffer(ref valuesBuffer, valueCount);
            for (var i = 0; i < valueCount; i++)
                typed[i] = ((payload[i >> 3] >> (i & 7)) & 1) != 0;
            values = new ReadOnlyMemory<T>(valuesBuffer!, 0, (int)valueCount);
            return true;
        }

        if (typeof(T) == typeof(int) && column.PhysicalType == ParquetPhysicalType.Int32)
        {
            ValidatePlainPayload(payload, valueCount, sizeof(int));
            var typed = (int[])(object)EnsureManagedBuffer(ref valuesBuffer, valueCount);
            CopyLittleEndianInt32(payload, typed.AsSpan(0, (int)valueCount));
            values = new ReadOnlyMemory<T>(valuesBuffer!, 0, (int)valueCount);
            return true;
        }

        if (typeof(T) == typeof(byte) && column.PhysicalType == ParquetPhysicalType.Int32)
        {
            ValidatePlainPayload(payload, valueCount, sizeof(int));
            var typed = (byte[])(object)EnsureManagedBuffer(ref valuesBuffer, valueCount);
            for (var i = 0; i < valueCount; i++)
                typed[i] = unchecked((byte)BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(i * 4, 4)));
            values = new ReadOnlyMemory<T>(valuesBuffer!, 0, (int)valueCount);
            return true;
        }

        if (typeof(T) == typeof(ushort) && column.PhysicalType == ParquetPhysicalType.Int32)
        {
            ValidatePlainPayload(payload, valueCount, sizeof(int));
            var typed = (ushort[])(object)EnsureManagedBuffer(ref valuesBuffer, valueCount);
            for (var i = 0; i < valueCount; i++)
                typed[i] = unchecked((ushort)BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(i * 4, 4)));
            values = new ReadOnlyMemory<T>(valuesBuffer!, 0, (int)valueCount);
            return true;
        }

        if (typeof(T) == typeof(uint) && column.PhysicalType == ParquetPhysicalType.Int32)
        {
            ValidatePlainPayload(payload, valueCount, sizeof(uint));
            var typed = (uint[])(object)EnsureManagedBuffer(ref valuesBuffer, valueCount);
            CopyLittleEndianUInt32(payload, typed.AsSpan(0, (int)valueCount));
            values = new ReadOnlyMemory<T>(valuesBuffer!, 0, (int)valueCount);
            return true;
        }

        if (typeof(T) == typeof(long) && column.PhysicalType == ParquetPhysicalType.Int64)
        {
            ValidatePlainPayload(payload, valueCount, sizeof(long));
            var typed = (long[])(object)EnsureManagedBuffer(ref valuesBuffer, valueCount);
            CopyLittleEndianInt64(payload, typed.AsSpan(0, (int)valueCount));
            values = new ReadOnlyMemory<T>(valuesBuffer!, 0, (int)valueCount);
            return true;
        }

        if (typeof(T) == typeof(ulong) && column.PhysicalType == ParquetPhysicalType.Int64)
        {
            ValidatePlainPayload(payload, valueCount, sizeof(ulong));
            var typed = (ulong[])(object)EnsureManagedBuffer(ref valuesBuffer, valueCount);
            CopyLittleEndianUInt64(payload, typed.AsSpan(0, (int)valueCount));
            values = new ReadOnlyMemory<T>(valuesBuffer!, 0, (int)valueCount);
            return true;
        }

        if (typeof(T) == typeof(float) && column.PhysicalType == ParquetPhysicalType.Float)
        {
            ValidatePlainPayload(payload, valueCount, sizeof(float));
            var typed = (float[])(object)EnsureManagedBuffer(ref valuesBuffer, valueCount);
            CopyLittleEndianFloat(payload, typed.AsSpan(0, (int)valueCount));
            values = new ReadOnlyMemory<T>(valuesBuffer!, 0, (int)valueCount);
            return true;
        }

        if (typeof(T) == typeof(double) && column.PhysicalType == ParquetPhysicalType.Double)
        {
            ValidatePlainPayload(payload, valueCount, sizeof(double));
            var typed = (double[])(object)EnsureManagedBuffer(ref valuesBuffer, valueCount);
            CopyLittleEndianDouble(payload, typed.AsSpan(0, (int)valueCount));
            values = new ReadOnlyMemory<T>(valuesBuffer!, 0, (int)valueCount);
            return true;
        }

        if (typeof(T) == typeof(DateOnly) && column.PhysicalType == ParquetPhysicalType.Int32)
        {
            ValidatePlainPayload(payload, valueCount, sizeof(int));
            var typed = (DateOnly[])(object)EnsureManagedBuffer(ref valuesBuffer, valueCount);
            for (var i = 0; i < valueCount; i++)
            {
                var days = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(i * 4, 4));
                typed[i] = DateOnly.FromDayNumber(days);
            }
            values = new ReadOnlyMemory<T>(valuesBuffer!, 0, (int)valueCount);
            return true;
        }

        if (typeof(T) == typeof(DateTimeOffset) && column.PhysicalType == ParquetPhysicalType.Int64)
        {
            ValidatePlainPayload(payload, valueCount, sizeof(long));
            var typed = (DateTimeOffset[])(object)EnsureManagedBuffer(ref valuesBuffer, valueCount);
            for (var i = 0; i < valueCount; i++)
            {
                var raw = BinaryPrimitives.ReadInt64LittleEndian(payload.Slice(i * 8, 8));
                typed[i] = DecodeTimestamp(raw, column.LogicalType);
            }
            values = new ReadOnlyMemory<T>(valuesBuffer!, 0, (int)valueCount);
            return true;
        }

        if (typeof(T) == typeof(DateTime) && column.PhysicalType == ParquetPhysicalType.Int64)
        {
            ValidatePlainPayload(payload, valueCount, sizeof(long));
            var typed = (DateTime[])(object)EnsureManagedBuffer(ref valuesBuffer, valueCount);
            for (var i = 0; i < valueCount; i++)
            {
                var raw = BinaryPrimitives.ReadInt64LittleEndian(payload.Slice(i * 8, 8));
                typed[i] = DecodeDateTime(raw, column.LogicalType);
            }
            values = new ReadOnlyMemory<T>(valuesBuffer!, 0, (int)valueCount);
            return true;
        }

        values = default;
        return false;
    }

    static bool TryDecodeBooleanRleIntoBuffer<T>(ReadOnlySpan<byte> payload, uint valueCount,
        ref T[]? valuesBuffer, out ReadOnlyMemory<T> values)
    {
        if (typeof(T) != typeof(bool))
        {
            values = default;
            return false;
        }

        var typed = (bool[])(object)EnsureManagedBuffer(ref valuesBuffer, valueCount);
        DecodeBooleanRle(payload, typed.AsSpan(0, (int)valueCount));
        values = new ReadOnlyMemory<T>(valuesBuffer!, 0, (int)valueCount);
        return true;
    }

    static bool TryDecodeByteStreamSplitIntoBuffer<T>(ReadOnlySpan<byte> payload, Column column, uint valueCount,
        ref T[]? valuesBuffer, out ReadOnlyMemory<T> values)
    {
        switch (column.PhysicalType)
        {
            case ParquetPhysicalType.Int32 when typeof(T) == typeof(int):
            {
                var typed = (int[])(object)EnsureManagedBuffer(ref valuesBuffer, valueCount);
                DecodeByteStreamSplitInt32(payload, typed.AsSpan(0, (int)valueCount));
                values = new ReadOnlyMemory<T>(valuesBuffer!, 0, (int)valueCount);
                return true;
            }
            case ParquetPhysicalType.Int32 when typeof(T) == typeof(byte):
            {
                var typed = (byte[])(object)EnsureManagedBuffer(ref valuesBuffer, valueCount);
                for (var i = 0; i < valueCount; i++)
                    typed[i] = (byte)(payload[i] | (payload[(int)valueCount + i] << 8) | (payload[((int)valueCount * 2) + i] << 16) |
                        (payload[((int)valueCount * 3) + i] << 24));
                values = new ReadOnlyMemory<T>(valuesBuffer!, 0, (int)valueCount);
                return true;
            }
            case ParquetPhysicalType.Int32 when typeof(T) == typeof(ushort):
            {
                var typed = (ushort[])(object)EnsureManagedBuffer(ref valuesBuffer, valueCount);
                for (var i = 0; i < valueCount; i++)
                    typed[i] = unchecked((ushort)(payload[i] | (payload[(int)valueCount + i] << 8) |
                        (payload[((int)valueCount * 2) + i] << 16) | (payload[((int)valueCount * 3) + i] << 24)));
                values = new ReadOnlyMemory<T>(valuesBuffer!, 0, (int)valueCount);
                return true;
            }
            case ParquetPhysicalType.Int32 when typeof(T) == typeof(uint):
            {
                var typed = (uint[])(object)EnsureManagedBuffer(ref valuesBuffer, valueCount);
                for (var i = 0; i < valueCount; i++)
                    typed[i] = unchecked((uint)(payload[i] | (payload[(int)valueCount + i] << 8) |
                        (payload[((int)valueCount * 2) + i] << 16) | (payload[((int)valueCount * 3) + i] << 24)));
                values = new ReadOnlyMemory<T>(valuesBuffer!, 0, (int)valueCount);
                return true;
            }
            case ParquetPhysicalType.Int64 when typeof(T) == typeof(long):
            {
                var typed = (long[])(object)EnsureManagedBuffer(ref valuesBuffer, valueCount);
                DecodeByteStreamSplitInt64(payload, typed.AsSpan(0, (int)valueCount));
                values = new ReadOnlyMemory<T>(valuesBuffer!, 0, (int)valueCount);
                return true;
            }
            case ParquetPhysicalType.Int64 when typeof(T) == typeof(ulong):
            {
                var typed = (ulong[])(object)EnsureManagedBuffer(ref valuesBuffer, valueCount);
                DecodeByteStreamSplitUInt64(payload, typed.AsSpan(0, (int)valueCount));
                values = new ReadOnlyMemory<T>(valuesBuffer!, 0, (int)valueCount);
                return true;
            }
            case ParquetPhysicalType.Float when typeof(T) == typeof(float):
            {
                var typed = (float[])(object)EnsureManagedBuffer(ref valuesBuffer, valueCount);
                DecodeByteStreamSplitFloat(payload, typed.AsSpan(0, (int)valueCount));
                values = new ReadOnlyMemory<T>(valuesBuffer!, 0, (int)valueCount);
                return true;
            }
            case ParquetPhysicalType.Double when typeof(T) == typeof(double):
            {
                var typed = (double[])(object)EnsureManagedBuffer(ref valuesBuffer, valueCount);
                DecodeByteStreamSplitDouble(payload, typed.AsSpan(0, (int)valueCount));
                values = new ReadOnlyMemory<T>(valuesBuffer!, 0, (int)valueCount);
                return true;
            }
            default:
                values = default;
                return false;
        }
    }

    static bool TryDecodeDeltaBinaryPackedIntoBuffer<T>(ReadOnlySpan<byte> payload, Column column, uint valueCount,
        ref T[]? valuesBuffer, out ReadOnlyMemory<T> values)
    {
        switch (column.PhysicalType)
        {
            case ParquetPhysicalType.Int32 when typeof(T) == typeof(int):
            {
                var typed = (int[])(object)EnsureManagedBuffer(ref valuesBuffer, valueCount);
                DeltaBinaryPackedDecoder.ReadInt32(payload, typed.AsSpan(0, (int)valueCount));
                values = new ReadOnlyMemory<T>(valuesBuffer!, 0, (int)valueCount);
                return true;
            }
            case ParquetPhysicalType.Int64 when typeof(T) == typeof(long):
            {
                var typed = (long[])(object)EnsureManagedBuffer(ref valuesBuffer, valueCount);
                DeltaBinaryPackedDecoder.ReadInt64(payload, typed.AsSpan(0, (int)valueCount));
                values = new ReadOnlyMemory<T>(valuesBuffer!, 0, (int)valueCount);
                return true;
            }
            default:
                values = default;
                return false;
        }
    }

    static bool TryDecodeDeltaLengthByteArrayIntoBuffer<T>(ReadOnlySpan<byte> payload, Column column, uint valueCount,
        ref T[]? valuesBuffer, IParquetBufferPool bufferPool, ref ParquetBuffer scratchBuffer,
        out ReadOnlyMemory<T> values)
    {
        if (typeof(T) != typeof(byte[]) || column.PhysicalType != ParquetPhysicalType.ByteArray)
        {
            values = default;
            return false;
        }

        var lengths = EnsureInt32Scratch(ref scratchBuffer, checked((int)valueCount), bufferPool);
        var consumedLengthBytes = DeltaBinaryPackedDecoder.ReadNonNegativeInt32WithConsumedBytes(payload,
            lengths);
        var remaining = payload[consumedLengthBytes..];
        var byteArrays = (byte[]?[])(object)EnsureManagedBuffer(ref valuesBuffer, valueCount);
        for (var i = 0; i < valueCount; i++)
        {
            var length = lengths[i];
            if (length > remaining.Length)
                throw new CorruptParquetException(
                    $"Delta length byte array entry {i} claims {length} bytes but only {remaining.Length} remain.");

            var value = EnsureExactByteArray(ref byteArrays[i], length);
            remaining[..length].CopyTo(value);
            remaining = remaining[length..];
        }

        values = new ReadOnlyMemory<T>(valuesBuffer!, 0, checked((int)valueCount));
        return true;
    }

    static bool TryDecodeDeltaByteArrayIntoBuffer<T>(ReadOnlySpan<byte> payload, Column column, uint valueCount,
        ref T[]? valuesBuffer, IParquetBufferPool bufferPool, ref ParquetBuffer scratchBuffer,
        out ReadOnlyMemory<T> values)
    {
        if (typeof(T) != typeof(byte[]) || column.PhysicalType != ParquetPhysicalType.ByteArray)
        {
            values = default;
            return false;
        }

        var scratch = EnsureInt32Scratch(ref scratchBuffer, checked((int)valueCount * 2), bufferPool);
        var prefixLengths = scratch[..checked((int)valueCount)];
        var prefixConsumed = DeltaBinaryPackedDecoder.ReadNonNegativeInt32WithConsumedBytes(payload,
            prefixLengths);
        var suffixPayload = payload[prefixConsumed..];
        var suffixLengths = scratch[checked((int)valueCount)..];
        var suffixConsumed = DeltaBinaryPackedDecoder.ReadNonNegativeInt32WithConsumedBytes(suffixPayload,
            suffixLengths);
        var suffixRemaining = suffixPayload[suffixConsumed..];
        var byteArrays = (byte[]?[])(object)EnsureManagedBuffer(ref valuesBuffer, valueCount);

        for (var i = 0; i < valueCount; i++)
        {
            var prefixLength = prefixLengths[i];
            var suffixLength = suffixLengths[i];
            var prevLength = i > 0 ? byteArrays[i - 1]!.Length : 0;
            if (prefixLength > prevLength)
                throw new CorruptParquetException(
                    $"Delta byte array prefix length {prefixLength} exceeds previous value length {prevLength}.");
            if (suffixLength > suffixRemaining.Length)
                throw new CorruptParquetException(
                    $"Delta byte array suffix length {suffixLength} exceeds remaining suffix bytes ({suffixRemaining.Length}).");
            var totalLength = prefixLength + suffixLength;
            var value = EnsureExactByteArray(ref byteArrays[i], totalLength);
            if (prefixLength > 0)
                byteArrays[i - 1]!.AsSpan(0, prefixLength).CopyTo(value);
            suffixRemaining[..suffixLength].CopyTo(value.AsSpan(prefixLength));
            suffixRemaining = suffixRemaining[suffixLength..];
        }

        values = new ReadOnlyMemory<T>(valuesBuffer!, 0, checked((int)valueCount));
        return true;
    }

    static byte[] EnsureExactByteArray(ref byte[]? buffer, int length)
    {
        if (buffer is null || buffer.Length != length)
            buffer = new byte[length];
        return buffer;
    }

    static void EnsureByteBuffer(ref ParquetBuffer buffer, int minimumLength, IParquetBufferPool bufferPool)
    {
        if (buffer.Length < minimumLength)
        {
            buffer.Dispose();
            buffer = minimumLength == 0 ? default : bufferPool.Rent(checked((uint)minimumLength));
        }
    }

    static Span<int> EnsureInt32Scratch(ref ParquetBuffer buffer, int minimumLength,
        IParquetBufferPool bufferPool)
    {
        var minimumByteLength = checked(minimumLength * sizeof(int));
        if (buffer.Length < minimumByteLength)
        {
            buffer.Dispose();
            buffer = minimumByteLength == 0
                ? default
                : bufferPool.Rent(checked((uint)minimumByteLength));
        }

        return minimumLength == 0 ? [] : ParquetBuffer.AsSpan<int>(buffer, minimumLength);
    }

    static void CopyLittleEndianInt32(ReadOnlySpan<byte> source, Span<int> destination)
    {
        if (BitConverter.IsLittleEndian)
        {
            source[..checked(destination.Length * sizeof(int))].CopyTo(MemoryMarshal.AsBytes(destination));
            return;
        }

        for (var i = 0; i < destination.Length; i++)
            destination[i] = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(i * sizeof(int), sizeof(int)));
    }

    static void CopyLittleEndianUInt32(ReadOnlySpan<byte> source, Span<uint> destination)
    {
        if (BitConverter.IsLittleEndian)
        {
            source[..checked(destination.Length * sizeof(uint))].CopyTo(MemoryMarshal.AsBytes(destination));
            return;
        }

        for (var i = 0; i < destination.Length; i++)
            destination[i] = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(i * sizeof(uint), sizeof(uint)));
    }

    static void CopyLittleEndianInt64(ReadOnlySpan<byte> source, Span<long> destination)
    {
        if (BitConverter.IsLittleEndian)
        {
            source[..checked(destination.Length * sizeof(long))].CopyTo(MemoryMarshal.AsBytes(destination));
            return;
        }

        for (var i = 0; i < destination.Length; i++)
            destination[i] = BinaryPrimitives.ReadInt64LittleEndian(source.Slice(i * sizeof(long), sizeof(long)));
    }

    static void CopyLittleEndianUInt64(ReadOnlySpan<byte> source, Span<ulong> destination)
    {
        if (BitConverter.IsLittleEndian)
        {
            source[..checked(destination.Length * sizeof(ulong))].CopyTo(MemoryMarshal.AsBytes(destination));
            return;
        }

        for (var i = 0; i < destination.Length; i++)
            destination[i] = BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(i * sizeof(ulong), sizeof(ulong)));
    }

    static void CopyLittleEndianFloat(ReadOnlySpan<byte> source, Span<float> destination)
    {
        if (BitConverter.IsLittleEndian)
        {
            source[..checked(destination.Length * sizeof(float))].CopyTo(MemoryMarshal.AsBytes(destination));
            return;
        }

        for (var i = 0; i < destination.Length; i++)
        {
            var bits = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(i * sizeof(float), sizeof(float)));
            destination[i] = BitConverter.Int32BitsToSingle(bits);
        }
    }

    static void CopyLittleEndianDouble(ReadOnlySpan<byte> source, Span<double> destination)
    {
        if (BitConverter.IsLittleEndian)
        {
            source[..checked(destination.Length * sizeof(double))].CopyTo(MemoryMarshal.AsBytes(destination));
            return;
        }

        for (var i = 0; i < destination.Length; i++)
        {
            var bits = BinaryPrimitives.ReadInt64LittleEndian(source.Slice(i * sizeof(double), sizeof(double)));
            destination[i] = BitConverter.Int64BitsToDouble(bits);
        }
    }

    static void DecodeByteStreamSplitInt32(ReadOnlySpan<byte> payload, Span<int> destination)
    {
        var count = destination.Length;
        if ((long)count * 4 > payload.Length)
            throw new CorruptParquetException(
                $"ByteStreamSplit payload ({payload.Length} bytes) is too short for {count} Int32 values.");
        var lane1 = count;
        var lane2 = count * 2;
        var lane3 = count * 3;
        for (var i = 0; i < count; i++)
            destination[i] = payload[i] | (payload[lane1 + i] << 8) | (payload[lane2 + i] << 16) |
                (payload[lane3 + i] << 24);
    }

    static void DecodeByteStreamSplitInt64(ReadOnlySpan<byte> payload, Span<long> destination)
    {
        var uintDestination = MemoryMarshal.Cast<long, ulong>(destination);
        DecodeByteStreamSplitUInt64(payload, uintDestination);
    }

    static void DecodeByteStreamSplitUInt64(ReadOnlySpan<byte> payload, Span<ulong> destination)
    {
        var count = destination.Length;
        if ((long)count * 8 > payload.Length)
            throw new CorruptParquetException(
                $"ByteStreamSplit payload ({payload.Length} bytes) is too short for {count} 8-byte values.");
        var lane1 = count;
        var lane2 = count * 2;
        var lane3 = count * 3;
        var lane4 = count * 4;
        var lane5 = count * 5;
        var lane6 = count * 6;
        var lane7 = count * 7;
        for (var i = 0; i < count; i++)
            destination[i] =
                (ulong)payload[i] |
                ((ulong)payload[lane1 + i] << 8) |
                ((ulong)payload[lane2 + i] << 16) |
                ((ulong)payload[lane3 + i] << 24) |
                ((ulong)payload[lane4 + i] << 32) |
                ((ulong)payload[lane5 + i] << 40) |
                ((ulong)payload[lane6 + i] << 48) |
                ((ulong)payload[lane7 + i] << 56);
    }

    static void DecodeByteStreamSplitFloat(ReadOnlySpan<byte> payload, Span<float> destination)
    {
        var intDestination = MemoryMarshal.Cast<float, int>(destination);
        DecodeByteStreamSplitInt32(payload, intDestination);
    }

    static void DecodeByteStreamSplitDouble(ReadOnlySpan<byte> payload, Span<double> destination)
    {
        var longDestination = MemoryMarshal.Cast<double, ulong>(destination);
        DecodeByteStreamSplitUInt64(payload, longDestination);
    }

    static Array DecodeDictionaryPage<T>(ReadOnlySpan<byte> payload, Column column, PageHeader header,
        CompressionKind compression, ref T[]? dictionaryBuffer, Type physicalDecodeType, IParquetBufferPool bufferPool)
    {
        var effectivePayload = compression == CompressionKind.None || header.CompressedPageSize == 0
            ? payload
            : ParquetDecompressor.Decompress(payload, header.UncompressedPageSize, compression);

        if ((ulong)header.ValueCount > (ulong)effectivePayload.Length * 8)
            throw new CorruptParquetException(
                $"Dictionary page value count ({header.ValueCount}) cannot be encoded in {effectivePayload.Length} bytes.");

        ParquetBuffer scratchBuffer = default;
        try
        {
            if (physicalDecodeType == typeof(T) &&
                TryDecodeValuesIntoBuffer(effectivePayload, column, header.ValueCount, header.Encoding,
                    ref dictionaryBuffer, bufferPool, ref scratchBuffer, out _))
                return dictionaryBuffer!;

            return DecodeValues(effectivePayload, column, header.ValueCount, header.Encoding, physicalDecodeType);
        }
        finally
        {
            scratchBuffer.Dispose();
        }
    }

    static T[] EnsureManagedBuffer<T>(ref T[]? buffer, uint length)
    {
        if (buffer is null || (uint)buffer.Length != length)
            buffer = new T[checked((int)length)];
        return buffer;
    }

    static void DecodeDictionaryIndexes<T>(ReadOnlySpan<byte> payload, uint valueCount, Array dictionary,
        ref T[]? valuesBuffer, out ReadOnlyMemory<T> values)
    {
        var result = EnsureManagedBuffer(ref valuesBuffer, valueCount);
        if (dictionary is T[] typedDictionary)
        {
            DecodeDictionaryIndexesIntoBuffer(payload, valueCount, typedDictionary, result);
        }
        else
        {
            var indexes = ReadRleBitPackedHybrid(payload, valueCount, hasBitWidthPrefix: true);
            for (var i = 0; i < indexes.Length; i++)
                result[i] = (T)dictionary.GetValue(indexes[i])!;
        }

        values = new ReadOnlyMemory<T>(result, 0, (int)valueCount);
    }

    static T[] DecodeDictionaryIndexesWithNulls<T>(ReadOnlySpan<byte> dataPayload, uint totalValueCount,
        uint physicalValueCount, Array dictionary, ReadOnlySpan<byte> definitionPayload,
        EncodingKind definitionLevelEncoding)
    {
        var indexes = ReadRleBitPackedHybrid(dataPayload, physicalValueCount, hasBitWidthPrefix: true);
        var definitionLevels = ReadDefinitionLevels(definitionPayload, totalValueCount,
            definitionLevelEncoding, out _);
        var result = new T[(int)totalValueCount];
        var valueIndex = 0;
        for (var i = 0; i < totalValueCount; i++)
        {
            if (definitionLevels[i] != 0)
            {
                if (valueIndex >= indexes.Length)
                    throw new CorruptParquetException(
                        $"Definition levels claim more non-null values than page header's physical count ({physicalValueCount}).");
                var dictIndex = indexes[valueIndex++];
                if ((uint)dictIndex >= (uint)dictionary.Length)
                    throw new CorruptParquetException(
                        $"Dictionary index {dictIndex} is out of range for a dictionary of {dictionary.Length} entries.");
                result[i] = (T)dictionary.GetValue(dictIndex)!;
            }
        }
        return result;
    }

    static void DecodeDictionaryIndexesIntoBuffer<T>(ReadOnlySpan<byte> payload, uint valueCount,
        ReadOnlySpan<T> dictionary, Span<T> destination)
    {
        if (valueCount == 0)
            return;

        if (payload.IsEmpty)
            throw new CorruptParquetException("Dictionary payload is empty but value count is non-zero.");
        var bitWidth = payload[0];
        if (bitWidth > 32)
            throw new CorruptParquetException($"Dictionary bit width {bitWidth} exceeds the maximum of 32.");
        payload = payload[1..];
        var valueIndex = 0U;
        while (valueIndex < valueCount)
        {
            var header = ReadUnsignedVarInt(ref payload);
            if ((header & 1U) == 0)
            {
                var runLength = header >> 1;
                var byteWidth = (bitWidth + 7) >> 3;
                var dictionaryIndex = byteWidth == 0 ? 0 : ReadLittleEndian(ref payload, byteWidth);
                if ((uint)dictionaryIndex >= (uint)dictionary.Length)
                    throw new CorruptParquetException(
                        $"Dictionary index {dictionaryIndex} is out of range for a dictionary of {dictionary.Length} entries.");
                var repeated = dictionary[dictionaryIndex];
                var copyLength = Math.Min(runLength, valueCount - valueIndex);
                destination.Slice((int)valueIndex, (int)copyLength).Fill(repeated);
                valueIndex += copyLength;
                continue;
            }

            var literalCount = (header >> 1) * 8U;
            var literalByteCount = ((literalCount * bitWidth) + 7) >> 3;
            if (literalByteCount > (uint)payload.Length)
                throw new CorruptParquetException(
                    $"Literal run claims {literalByteCount} bytes but only {payload.Length} remain.");
            var literalPayload = payload[..(int)literalByteCount];
            var literalCopyLength = Math.Min(literalCount, valueCount - valueIndex);
            DecodeDictionaryLiteralIndexes(literalPayload, bitWidth, dictionary,
                destination.Slice((int)valueIndex, (int)literalCopyLength));
            valueIndex += literalCopyLength;
            payload = payload[(int)literalByteCount..];
        }
    }

    static void DecodeDictionaryIndexesIntoBuffer(ReadOnlySpan<byte> payload, uint valueCount,
        int dictionaryLength, Span<int> destination)
    {
        if (valueCount == 0)
            return;
        if (payload.IsEmpty)
            throw new CorruptParquetException("Dictionary payload is empty but value count is non-zero.");

        var bitWidth = payload[0];
        if (bitWidth > 32)
            throw new CorruptParquetException($"Dictionary bit width {bitWidth} exceeds the maximum of 32.");
        payload = payload[1..];
        var valueIndex = 0U;
        while (valueIndex < valueCount)
        {
            var header = ReadUnsignedVarInt(ref payload);
            if ((header & 1U) == 0)
            {
                var runLength = header >> 1;
                if (runLength == 0)
                    throw new CorruptParquetException("Dictionary RLE run length must be positive.");
                var byteWidth = (bitWidth + 7) >> 3;
                var dictionaryIndex = byteWidth == 0 ? 0 : ReadLittleEndian(ref payload, byteWidth);
                ValidateDictionaryIndex(dictionaryIndex, dictionaryLength);
                var copyLength = Math.Min(runLength, valueCount - valueIndex);
                destination.Slice((int)valueIndex, (int)copyLength).Fill(dictionaryIndex);
                valueIndex += copyLength;
                continue;
            }

            var literalGroupCount = header >> 1;
            if (literalGroupCount == 0 || literalGroupCount > uint.MaxValue / 8)
                throw new CorruptParquetException(
                    $"Dictionary literal run group count {literalGroupCount} is invalid.");
            var literalCount = literalGroupCount * 8U;
            var literalByteCount = (((ulong)literalCount * bitWidth) + 7) >> 3;
            if (literalByteCount > (uint)payload.Length)
                throw new CorruptParquetException(
                    $"Literal run claims {literalByteCount} bytes but only {payload.Length} remain.");
            var literalPayload = payload[..(int)literalByteCount];
            var literalCopyLength = Math.Min(literalCount, valueCount - valueIndex);
            DecodeDictionaryLiteralIndexes(literalPayload, bitWidth, dictionaryLength,
                destination.Slice((int)valueIndex, (int)literalCopyLength));
            valueIndex += literalCopyLength;
            payload = payload[(int)literalByteCount..];
        }
    }

    static void DecodeDictionaryLiteralIndexes(ReadOnlySpan<byte> payload, int bitWidth,
        int dictionaryLength, Span<int> destination)
    {
        if (bitWidth == 0)
        {
            ValidateDictionaryIndex(0, dictionaryLength);
            destination.Clear();
            return;
        }

        var mask = bitWidth == 32 ? uint.MaxValue : (1UL << bitWidth) - 1UL;
        ulong bitBuffer = 0;
        var bufferedBits = 0;
        var byteIndex = 0;
        for (var i = 0; i < destination.Length; i++)
        {
            while (bufferedBits < bitWidth)
            {
                bitBuffer |= (ulong)payload[byteIndex++] << bufferedBits;
                bufferedBits += 8;
            }

            var rawDictionaryIndex = bitBuffer & mask;
            if (rawDictionaryIndex > int.MaxValue)
                throw new CorruptParquetException(
                    $"Dictionary index {rawDictionaryIndex} exceeds the supported maximum of {int.MaxValue}.");
            var dictionaryIndex = checked((int)rawDictionaryIndex);
            bitBuffer >>= bitWidth;
            bufferedBits -= bitWidth;
            ValidateDictionaryIndex(dictionaryIndex, dictionaryLength);
            destination[i] = dictionaryIndex;
        }
    }

    static void ValidateDictionaryIndex(int dictionaryIndex, int dictionaryLength)
    {
        if ((uint)dictionaryIndex >= (uint)dictionaryLength)
            throw new CorruptParquetException(
                $"Dictionary index {dictionaryIndex} is out of range for a dictionary of {dictionaryLength} entries.");
    }

    static void DecodeDictionaryLiteralIndexes<T>(ReadOnlySpan<byte> payload, int bitWidth,
        ReadOnlySpan<T> dictionary,
        Span<T> destination)
    {
        if (bitWidth == 0)
        {
            if (dictionary.Length == 0)
                throw new CorruptParquetException("Dictionary is empty but values reference index 0.");
            destination.Fill(dictionary[0]);
            return;
        }

        var mask = bitWidth == 32 ? ulong.MaxValue : (1UL << bitWidth) - 1UL;
        ulong bitBuffer = 0;
        var bufferedBits = 0;
        var byteIndex = 0;
        for (var i = 0; i < destination.Length; i++)
        {
            while (bufferedBits < bitWidth)
            {
                bitBuffer |= (ulong)payload[byteIndex++] << bufferedBits;
                bufferedBits += 8;
            }

            var dictionaryIndex = (int)(bitBuffer & mask);
            bitBuffer >>= bitWidth;
            bufferedBits -= bitWidth;
            if ((uint)dictionaryIndex >= (uint)dictionary.Length)
                throw new CorruptParquetException(
                    $"Dictionary index {dictionaryIndex} is out of range for a dictionary of {dictionary.Length} entries.");
            destination[i] = dictionary[dictionaryIndex];
        }
    }

    static Array DecodeValues(ReadOnlySpan<byte> payload, Column column, PageHeader header, CompressionKind compression,
        Type targetType)
    {
        var bytes = compression == CompressionKind.None || header.CompressedPageSize == 0
            ? payload.ToArray()
            : ParquetDecompressor.Decompress(payload, header.UncompressedPageSize, compression);
        return DecodeValues(bytes, column, header.ValueCount, header.Encoding, targetType);
    }

    static Array DecodeValues(ReadOnlySpan<byte> payload, Column column, uint valueCount, EncodingKind encoding,
        Type targetType)
    {
        switch (encoding)
        {
            case EncodingKind.Plain:
                return DecodePlain(payload, column, valueCount, targetType);
            case EncodingKind.Rle:
                return DecodeBooleanRle(payload, valueCount, targetType);
            case EncodingKind.ByteStreamSplit:
                return DecodeByteStreamSplit(payload, column, valueCount, targetType);
            case EncodingKind.DeltaBinaryPacked:
                return DecodeDeltaBinaryPacked(payload, column, targetType);
            case EncodingKind.DeltaLengthByteArray:
                return DecodeDeltaLengthByteArray(payload, targetType);
            case EncodingKind.DeltaByteArray:
                return DecodeDeltaByteArray(payload, targetType);
            default:
                throw new NotSupportedException($"Encoding '{encoding}' is not supported.");
        }
    }

    static Array DecodePlain(ReadOnlySpan<byte> payload, Column column, uint valueCount, Type targetType)
        => column.PhysicalType switch
        {
            ParquetPhysicalType.Boolean => DecodePlainBoolean(payload, valueCount, targetType),
            ParquetPhysicalType.Int32 => DecodePlainInt32(payload, valueCount, column.LogicalType, targetType),
            ParquetPhysicalType.Int64 => DecodePlainInt64(payload, valueCount, column.LogicalType, targetType),
            ParquetPhysicalType.Float => DecodePlainFloat(payload, valueCount, targetType),
            ParquetPhysicalType.Double => DecodePlainDouble(payload, valueCount, targetType),
            ParquetPhysicalType.ByteArray => DecodePlainByteArray(payload, valueCount, targetType),
            ParquetPhysicalType.FixedLenByteArray => DecodeFixedLengthByteArray(payload, valueCount,
                checked((int)column.Options.TypeLength), targetType),
            ParquetPhysicalType.Int96 => DecodeFixedLengthByteArray(payload, valueCount, 12, targetType),
            _ => throw new NotSupportedException($"Physical type '{column.PhysicalType}' is not supported.")
        };

    static Array DecodePlainBoolean(ReadOnlySpan<byte> payload, uint valueCount, Type targetType)
    {
        if (targetType != typeof(bool))
            throw new CorruptParquetException($"Boolean column cannot be projected to '{targetType}'.");

        if (valueCount > (uint)payload.Length * 8)
            throw new CorruptParquetException(
                $"Payload ({payload.Length} bytes) is too short to decode {valueCount} plain boolean values.");

        var values = new bool[(int)valueCount];
        for (var i = 0; i < valueCount; i++)
            values[i] = ((payload[i >> 3] >> (i & 7)) & 1) != 0;
        return values;
    }

    static Array DecodePlainInt32(ReadOnlySpan<byte> payload, uint valueCount, LogicalType? logicalType, Type targetType)
    {
        ValidatePlainPayload(payload, valueCount, sizeof(int));
        if (targetType == typeof(int))
        {
            var values = new int[(int)valueCount];
            for (var i = 0; i < valueCount; i++)
                values[i] = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(i * 4, 4));
            return values;
        }

        if (targetType == typeof(byte))
        {
            var values = new byte[valueCount];
            for (var i = 0; i < valueCount; i++)
                values[i] = unchecked((byte)BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(i * 4, 4)));
            return values;
        }

        if (targetType == typeof(ushort))
        {
            var values = new ushort[valueCount];
            for (var i = 0; i < valueCount; i++)
                values[i] = unchecked((ushort)BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(i * 4, 4)));
            return values;
        }

        if (targetType == typeof(uint))
        {
            var values = new uint[valueCount];
            for (var i = 0; i < valueCount; i++)
                values[i] = unchecked((uint)BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(i * 4, 4)));
            return values;
        }

        if (targetType == typeof(DateOnly) || logicalType is LogicalType.Date)
        {
            var values = new DateOnly[valueCount];
            for (var i = 0; i < valueCount; i++)
            {
                var days = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(i * 4, 4));
                values[i] = DateOnly.FromDayNumber(days);
            }
            return values;
        }

        throw new CorruptParquetException($"Int32 column cannot be projected to '{targetType}'.");
    }

    static Array DecodePlainInt64(ReadOnlySpan<byte> payload, uint valueCount, LogicalType? logicalType, Type targetType)
    {
        ValidatePlainPayload(payload, valueCount, sizeof(long));
        if (targetType == typeof(long))
        {
            var values = new long[valueCount];
            for (var i = 0; i < valueCount; i++)
                values[i] = BinaryPrimitives.ReadInt64LittleEndian(payload.Slice(i * 8, 8));
            return values;
        }

        if (targetType == typeof(ulong))
        {
            var values = new ulong[valueCount];
            for (var i = 0; i < valueCount; i++)
                values[i] = unchecked((ulong)BinaryPrimitives.ReadInt64LittleEndian(payload.Slice(i * 8, 8)));
            return values;
        }

        if (targetType == typeof(DateTimeOffset))
        {
            var values = new DateTimeOffset[valueCount];
            for (var i = 0; i < valueCount; i++)
            {
                var raw = BinaryPrimitives.ReadInt64LittleEndian(payload.Slice(i * 8, 8));
                values[i] = DecodeTimestamp(raw, logicalType);
            }
            return values;
        }

        if (targetType == typeof(DateTime))
        {
            var values = new DateTime[valueCount];
            for (var i = 0; i < valueCount; i++)
            {
                var raw = BinaryPrimitives.ReadInt64LittleEndian(payload.Slice(i * 8, 8));
                values[i] = DecodeDateTime(raw, logicalType);
            }
            return values;
        }

        throw new CorruptParquetException($"Int64 column cannot be projected to '{targetType}'.");
    }

    static Array DecodePlainFloat(ReadOnlySpan<byte> payload, uint valueCount, Type targetType)
    {
        ValidatePlainPayload(payload, valueCount, sizeof(float));
        if (targetType != typeof(float))
            throw new CorruptParquetException($"Float column cannot be projected to '{targetType}'.");

        var values = new float[valueCount];
        for (var i = 0; i < valueCount; i++)
        {
            var bits = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(i * 4, 4));
            values[i] = BitConverter.Int32BitsToSingle(bits);
        }
        return values;
    }

    static Array DecodePlainDouble(ReadOnlySpan<byte> payload, uint valueCount, Type targetType)
    {
        ValidatePlainPayload(payload, valueCount, sizeof(double));
        if (targetType != typeof(double))
            throw new CorruptParquetException($"Double column cannot be projected to '{targetType}'.");

        var values = new double[valueCount];
        for (var i = 0; i < valueCount; i++)
        {
            var bits = BinaryPrimitives.ReadInt64LittleEndian(payload.Slice(i * 8, 8));
            values[i] = BitConverter.Int64BitsToDouble(bits);
        }
        return values;
    }

    static Array DecodePlainByteArray(ReadOnlySpan<byte> payload, uint valueCount, Type targetType)
    {
        if (targetType == typeof(byte[]))
        {
            var values = new byte[valueCount][];
            var remaining = payload;
            for (var i = 0; i < valueCount; i++)
            {
                if (remaining.Length < 4)
                    throw new CorruptParquetException("Payload too short to read byte array length prefix.");
                var length = BinaryPrimitives.ReadUInt32LittleEndian(remaining);
                remaining = remaining[4..];
                if (length > (uint)remaining.Length)
                    throw new CorruptParquetException(
                        $"Byte array length {length} exceeds remaining payload ({remaining.Length} bytes).");
                values[i] = remaining[..checked((int)length)].ToArray();
                remaining = remaining[checked((int)length)..];
            }
            return values;
        }

        throw new CorruptParquetException($"Byte-array column cannot be projected to '{targetType}'.");
    }

    static Array DecodeFixedLengthByteArray(ReadOnlySpan<byte> payload, uint valueCount, int valueLength, Type targetType)
    {
        if (targetType != typeof(byte[]))
            throw new CorruptParquetException($"Fixed-length binary column cannot be projected to '{targetType}'.");

        var values = new byte[valueCount][];
        var offset = 0;
        for (var i = 0; i < valueCount; i++)
        {
            values[i] = payload.Slice(offset, valueLength).ToArray();
            offset += valueLength;
        }
        return values;
    }

    static Array DecodeBooleanRle(ReadOnlySpan<byte> payload, uint valueCount, Type targetType)
    {
        if (targetType != typeof(bool))
            throw new CorruptParquetException($"Boolean column cannot be projected to '{targetType}'.");

        payload = ReadBooleanRlePayload(payload);
        var ints = ReadRleBitPackedHybrid(payload, valueCount, bitWidth: 1);
        var values = new bool[ints.Length];
        for (var i = 0; i < ints.Length; i++)
            values[i] = ints[i] != 0;
        return values;
    }

    static Array DecodeByteStreamSplit(ReadOnlySpan<byte> payload, Column column, uint valueCount, Type targetType)
    {
        switch (column.PhysicalType)
        {
            case ParquetPhysicalType.Int32:
            {
                if ((long)valueCount * 4 > payload.Length)
                    throw new CorruptParquetException(
                        $"ByteStreamSplit payload ({payload.Length} bytes) is too short for {valueCount} Int32 values.");
                if (targetType == typeof(int))
                {
                    var values = new int[(int)valueCount];
                    for (var i = 0; i < valueCount; i++)
                        values[i] = payload[i] | (payload[(int)valueCount + i] << 8) | (payload[((int)valueCount * 2) + i] << 16) |
                            (payload[((int)valueCount * 3) + i] << 24);
                    return values;
                }
                if (targetType == typeof(byte))
                {
                    var values = new byte[valueCount];
                    for (var i = 0; i < valueCount; i++)
                        values[i] = (byte)(payload[i] | (payload[(int)valueCount + i] << 8) | (payload[((int)valueCount * 2) + i] << 16) |
                            (payload[((int)valueCount * 3) + i] << 24));
                    return values;
                }
                if (targetType == typeof(ushort))
                {
                    var values = new ushort[valueCount];
                    for (var i = 0; i < valueCount; i++)
                        values[i] = unchecked((ushort)(payload[i] | (payload[(int)valueCount + i] << 8) |
                            (payload[((int)valueCount * 2) + i] << 16) | (payload[((int)valueCount * 3) + i] << 24)));
                    return values;
                }
                if (targetType == typeof(uint))
                {
                    var values = new uint[valueCount];
                    for (var i = 0; i < valueCount; i++)
                        values[i] = unchecked((uint)(payload[i] | (payload[(int)valueCount + i] << 8) |
                            (payload[((int)valueCount * 2) + i] << 16) | (payload[((int)valueCount * 3) + i] << 24)));
                    return values;
                }
                throw new CorruptParquetException($"Int32 column cannot be projected to '{targetType}'.");
            }
            case ParquetPhysicalType.Int64:
            {
                if ((long)valueCount * 8 > payload.Length)
                    throw new CorruptParquetException(
                        $"ByteStreamSplit payload ({payload.Length} bytes) is too short for {valueCount} 8-byte values.");
                if (targetType == typeof(long))
                {
                    var values = new long[valueCount];
                    for (var i = 0; i < valueCount; i++)
                    {
                        ulong value = 0;
                        for (var lane = 0; lane < 8; lane++)
                            value |= (ulong)payload[((int)valueCount * lane) + i] << (lane * 8);
                        values[i] = unchecked((long)value);
                    }
                    return values;
                }
                if (targetType == typeof(ulong))
                {
                    var values = new ulong[valueCount];
                    for (var i = 0; i < valueCount; i++)
                    {
                        ulong value = 0;
                        for (var lane = 0; lane < 8; lane++)
                            value |= (ulong)payload[((int)valueCount * lane) + i] << (lane * 8);
                        values[i] = value;
                    }
                    return values;
                }
                throw new CorruptParquetException($"Int64 column cannot be projected to '{targetType}'.");
            }
            case ParquetPhysicalType.Float:
            {
                var intValues = (int[])DecodeByteStreamSplit(payload, new Column(column.Name, ParquetPhysicalType.Int32),
                    valueCount, typeof(int));
                if (targetType != typeof(float))
                    throw new CorruptParquetException($"Float column cannot be projected to '{targetType}'.");
                var values = new float[intValues.Length];
                for (var i = 0; i < intValues.Length; i++)
                    values[i] = BitConverter.Int32BitsToSingle(intValues[i]);
                return values;
            }
            case ParquetPhysicalType.Double:
            {
                var longValues = (long[])DecodeByteStreamSplit(payload, new Column(column.Name, ParquetPhysicalType.Int64),
                    valueCount, typeof(long));
                if (targetType != typeof(double))
                    throw new CorruptParquetException($"Double column cannot be projected to '{targetType}'.");
                var values = new double[longValues.Length];
                for (var i = 0; i < longValues.Length; i++)
                    values[i] = BitConverter.Int64BitsToDouble(longValues[i]);
                return values;
            }
            default:
                throw new NotSupportedException(
                    $"Byte-stream-split decoding is not supported for physical type '{column.PhysicalType}'.");
        }
    }

    static Array DecodeDeltaBinaryPacked(ReadOnlySpan<byte> payload, Column column, Type targetType)
    {
        if (column.PhysicalType == ParquetPhysicalType.Int32)
        {
            var values = DeltaBinaryPackedDecoder.ReadInt32(payload);
            if (targetType == typeof(int))
                return values;
            if (targetType == typeof(byte))
            {
                var projected = new byte[values.Length];
                for (var i = 0; i < values.Length; i++)
                    projected[i] = unchecked((byte)values[i]);
                return projected;
            }
            if (targetType == typeof(ushort))
            {
                var projected = new ushort[values.Length];
                for (var i = 0; i < values.Length; i++)
                    projected[i] = unchecked((ushort)values[i]);
                return projected;
            }
            if (targetType == typeof(uint))
            {
                var projected = new uint[values.Length];
                for (var i = 0; i < values.Length; i++)
                    projected[i] = unchecked((uint)values[i]);
                return projected;
            }
            throw new CorruptParquetException($"Int32 column cannot be projected to '{targetType}'.");
        }

        if (column.PhysicalType == ParquetPhysicalType.Int64)
        {
            var values = DeltaBinaryPackedDecoder.ReadInt64(payload);
            if (targetType == typeof(long))
                return values;
            if (targetType == typeof(ulong))
            {
                var projected = new ulong[values.Length];
                for (var i = 0; i < values.Length; i++)
                    projected[i] = unchecked((ulong)values[i]);
                return projected;
            }
            throw new CorruptParquetException($"Int64 column cannot be projected to '{targetType}'.");
        }

        throw new NotSupportedException(
            $"Delta binary packed decoding is not supported for physical type '{column.PhysicalType}'.");
    }

    static Array DecodeDeltaLengthByteArray(ReadOnlySpan<byte> payload, Type targetType)
    {
        var (lengths, consumedLengthBytes) = DeltaBinaryPackedDecoder.ReadUInt32WithConsumedBytes(payload);
        var remaining = payload[consumedLengthBytes..];
        var values = new byte[lengths.Length][];
        for (var i = 0; i < lengths.Length; i++)
        {
            var length = lengths[i];
            if (length > (uint)remaining.Length)
                throw new CorruptParquetException(
                    $"Delta length byte array entry {i} claims {length} bytes but only {remaining.Length} remain.");
            values[i] = remaining[..(int)length].ToArray();
            remaining = remaining[(int)length..];
        }
        return targetType == typeof(byte[]) ? values : DecodePlainByteArray(ToLengthPrefixed(values), (uint)values.Length, targetType);
    }

    static Array DecodeDeltaByteArray(ReadOnlySpan<byte> payload, Type targetType)
    {
        var (prefixLengths, prefixConsumed) = DeltaBinaryPackedDecoder.ReadUInt32WithConsumedBytes(payload);
        var suffixPayload = payload[prefixConsumed..];
        var (suffixLengths, suffixConsumed) = DeltaBinaryPackedDecoder.ReadUInt32WithConsumedBytes(suffixPayload);
        var suffixBytes = suffixPayload[suffixConsumed..];

        if (suffixLengths.Length != prefixLengths.Length)
            throw new CorruptParquetException(
                $"Delta byte array prefix count {prefixLengths.Length} does not match suffix count {suffixLengths.Length}.");

        var values = new byte[prefixLengths.Length][];
        var suffixRemaining = suffixBytes;
        for (var i = 0; i < values.Length; i++)
        {
            var prefixLength = prefixLengths[i];
            var suffixLength = suffixLengths[i];
            var totalLength = prefixLength + suffixLength;
            if (totalLength < prefixLength)
                throw new CorruptParquetException(
                    $"Delta byte array value length overflow (prefix={prefixLength} + suffix={suffixLength}).");
            var value = new byte[(int)totalLength];
            if (prefixLength > 0 && i > 0)
            {
                if (prefixLength > (uint)values[i - 1].Length)
                    throw new CorruptParquetException(
                        $"Delta byte array prefix length {prefixLength} exceeds previous value length {values[i - 1].Length}.");
                values[i - 1].AsSpan(0, (int)prefixLength).CopyTo(value);
            }
            if (suffixLength > 0)
            {
                if (suffixLength > (uint)suffixRemaining.Length)
                    throw new CorruptParquetException(
                        $"Delta byte array suffix length {suffixLength} exceeds remaining suffix bytes ({suffixRemaining.Length}).");
                suffixRemaining[..(int)suffixLength].CopyTo(value.AsSpan((int)prefixLength));
                suffixRemaining = suffixRemaining[(int)suffixLength..];
            }
            values[i] = value;
        }

        return targetType == typeof(byte[]) ? values : DecodePlainByteArray(ToLengthPrefixed(values), (uint)values.Length, targetType);
    }

    static byte[] ToLengthPrefixed(byte[][] values)
    {
        var totalLength = 0;
        for (var i = 0; i < values.Length; i++)
            totalLength = checked(totalLength + 4 + values[i].Length);

        var buffer = new byte[totalLength];
        var offset = 0;
        for (var i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(offset, 4), values[i].Length);
            offset += 4;
            values[i].CopyTo(buffer.AsSpan(offset));
            offset += values[i].Length;
        }
        return buffer;
    }

    static Type GetPhysicalDecodeType<T>()
    {
        if (typeof(T) == typeof(int?)) return typeof(int);
        if (typeof(T) == typeof(long?)) return typeof(long);
        if (typeof(T) == typeof(bool?)) return typeof(bool);
        if (typeof(T) == typeof(float?)) return typeof(float);
        if (typeof(T) == typeof(double?)) return typeof(double);
        if (typeof(T) == typeof(byte?)) return typeof(byte);
        if (typeof(T) == typeof(ushort?)) return typeof(ushort);
        if (typeof(T) == typeof(uint?)) return typeof(uint);
        if (typeof(T) == typeof(ulong?)) return typeof(ulong);
        if (typeof(T) == typeof(DateOnly?)) return typeof(DateOnly);
        if (typeof(T) == typeof(DateTime?)) return typeof(DateTime);
        if (typeof(T) == typeof(DateTimeOffset?)) return typeof(DateTimeOffset);
        if (typeof(T) == typeof(TimeOnly?)) return typeof(TimeOnly);
        if (typeof(T) == typeof(ReadOnlyMemory<byte>?)) return typeof(ReadOnlyMemory<byte>);
        return typeof(T);
    }

    static ReadOnlyMemory<T> DecodeValuesWithNullExpansion<T>(ReadOnlySpan<byte> dataPayload,
        ReadOnlySpan<byte> definitionPayload, Column column, uint totalValueCount, uint physicalValueCount,
        EncodingKind encoding, EncodingKind definitionLevelEncoding, bool hasNulls)
    {
        var physicalDecodeType = GetPhysicalDecodeType<T>();
        var physicalValues = physicalValueCount > 0
            ? DecodeValues(dataPayload, column, physicalValueCount, encoding, physicalDecodeType)
            : Array.CreateInstance(physicalDecodeType, 0);

        if (!hasNulls)
            return ExpandAllPresent<T>(physicalValues, totalValueCount);

        var definitionLevels = ReadDefinitionLevels(definitionPayload, totalValueCount,
            definitionLevelEncoding, out var nonNullCount);
        if (nonNullCount != (int)physicalValueCount)
            throw new CorruptParquetException(
                $"Definition levels indicate {nonNullCount} non-null values but page header claimed {physicalValueCount}.");
        return ExpandWithDefinitionLevels<T>(physicalValues, definitionLevels, totalValueCount);
    }

    static ReadOnlyMemory<T> ExpandAllPresent<T>(Array physicalValues, uint valueCount)
    {
        var result = new T[(int)valueCount];
        if (typeof(T) == typeof(int?))
        {
            var src = (int[])physicalValues;
            var dst = (int?[])(object)result;
            for (var i = 0; i < valueCount; i++) dst[i] = src[i];
        }
        else if (typeof(T) == typeof(long?))
        {
            var src = (long[])physicalValues;
            var dst = (long?[])(object)result;
            for (var i = 0; i < valueCount; i++) dst[i] = src[i];
        }
        else if (typeof(T) == typeof(bool?))
        {
            var src = (bool[])physicalValues;
            var dst = (bool?[])(object)result;
            for (var i = 0; i < valueCount; i++) dst[i] = src[i];
        }
        else if (typeof(T) == typeof(float?))
        {
            var src = (float[])physicalValues;
            var dst = (float?[])(object)result;
            for (var i = 0; i < valueCount; i++) dst[i] = src[i];
        }
        else if (typeof(T) == typeof(double?))
        {
            var src = (double[])physicalValues;
            var dst = (double?[])(object)result;
            for (var i = 0; i < valueCount; i++) dst[i] = src[i];
        }
        else if (typeof(T) == typeof(byte?))
        {
            var src = (byte[])physicalValues;
            var dst = (byte?[])(object)result;
            for (var i = 0; i < valueCount; i++) dst[i] = src[i];
        }
        else if (typeof(T) == typeof(ushort?))
        {
            var src = (ushort[])physicalValues;
            var dst = (ushort?[])(object)result;
            for (var i = 0; i < valueCount; i++) dst[i] = src[i];
        }
        else if (typeof(T) == typeof(uint?))
        {
            var src = (uint[])physicalValues;
            var dst = (uint?[])(object)result;
            for (var i = 0; i < valueCount; i++) dst[i] = src[i];
        }
        else if (typeof(T) == typeof(ulong?))
        {
            var src = (ulong[])physicalValues;
            var dst = (ulong?[])(object)result;
            for (var i = 0; i < valueCount; i++) dst[i] = src[i];
        }
        else if (typeof(T) == typeof(DateOnly?))
        {
            var src = (DateOnly[])physicalValues;
            var dst = (DateOnly?[])(object)result;
            for (var i = 0; i < valueCount; i++) dst[i] = src[i];
        }
        else if (typeof(T) == typeof(DateTime?))
        {
            var src = (DateTime[])physicalValues;
            var dst = (DateTime?[])(object)result;
            for (var i = 0; i < valueCount; i++) dst[i] = src[i];
        }
        else if (typeof(T) == typeof(DateTimeOffset?))
        {
            var src = (DateTimeOffset[])physicalValues;
            var dst = (DateTimeOffset?[])(object)result;
            for (var i = 0; i < valueCount; i++) dst[i] = src[i];
        }
        else if (typeof(T) == typeof(TimeOnly?))
        {
            var src = (TimeOnly[])physicalValues;
            var dst = (TimeOnly?[])(object)result;
            for (var i = 0; i < valueCount; i++) dst[i] = src[i];
        }
        else if (typeof(T) == typeof(ReadOnlyMemory<byte>?))
        {
            var src = (ReadOnlyMemory<byte>[])physicalValues;
            var dst = (ReadOnlyMemory<byte>?[])(object)result;
            for (var i = 0; i < valueCount; i++) dst[i] = src[i];
        }
        else
        {
            for (var i = 0; i < valueCount; i++)
                result[i] = (T)physicalValues.GetValue(i)!;
        }
        return new ReadOnlyMemory<T>(result, 0, (int)valueCount);
    }

    static void DecodeDefinitionLevels(ReadOnlySpan<byte> payload, int valueCount, EncodingKind encoding,
        Span<int> destination, out int nonNullCount)
    {
        if (!destination.IsEmpty && destination.Length < valueCount)
            throw new ArgumentException("Definition-level destination is too small.", nameof(destination));

        if (encoding == EncodingKind.BitPacked)
        {
            if (destination.IsEmpty)
            {
                nonNullCount = LegacyBitPackedDecoder.CountSetBits(payload, valueCount);
                return;
            }

            var levels = destination[..valueCount];
            LegacyBitPackedDecoder.Decode(payload, bitWidth: 1, levels);
            nonNullCount = 0;
            for (var i = 0; i < levels.Length; i++)
                nonNullCount += levels[i];
            return;
        }
        if (encoding != EncodingKind.Rle)
            throw new NotSupportedException($"Definition level encoding '{encoding}' is not supported.");

        var valueIndex = 0;
        var count = 0;
        while (valueIndex < valueCount)
        {
            var header = ReadUnsignedVarInt(ref payload);
            if ((header & 1U) == 0)
            {
                var runLength = header >> 1;
                var repeated = ReadLittleEndian(ref payload, byteWidth: 1);
                var copyLength = (int)Math.Min(runLength, checked((uint)(valueCount - valueIndex)));
                if (!destination.IsEmpty)
                    destination.Slice(valueIndex, copyLength).Fill(repeated);
                if (repeated != 0)
                    count += copyLength;
                valueIndex += copyLength;
                continue;
            }

            var literalGroupCount = header >> 1;
            if (literalGroupCount > uint.MaxValue / 8)
                throw new CorruptParquetException(
                    $"Definition levels literal run group count {literalGroupCount} is too large.");
            var literalCount = literalGroupCount * 8U;
            for (var i = 0U; i < literalCount && valueIndex < valueCount; i++)
            {
                var value = ReadBitPackedValue(ref payload, bitWidth: 1, checked((int)i));
                if (!destination.IsEmpty)
                    destination[valueIndex] = value;
                valueIndex++;
                count += value;
            }

            var literalByteCount = (literalCount + 7U) >> 3;
            if (literalByteCount > (uint)payload.Length)
                throw new CorruptParquetException(
                    $"Definition level literal group claims {literalByteCount} bytes but only {payload.Length} remain.");
            payload = payload[checked((int)literalByteCount)..];
        }

        nonNullCount = count;
    }

    static int[] ReadDefinitionLevels(ReadOnlySpan<byte> payload, uint valueCount, EncodingKind encoding,
        out int nonNullCount)
    {
        var values = new int[(int)valueCount];
        if (encoding == EncodingKind.BitPacked)
        {
            LegacyBitPackedDecoder.Decode(payload, bitWidth: 1, values);
            nonNullCount = 0;
            for (var i = 0; i < values.Length; i++)
                nonNullCount += values[i];
            return values;
        }
        if (encoding != EncodingKind.Rle)
            throw new NotSupportedException($"Definition level encoding '{encoding}' is not supported.");

        var valueIndex = 0U;
        var count = 0;
        while (valueIndex < valueCount)
        {
            var header = ReadUnsignedVarInt(ref payload);
            if ((header & 1U) == 0)
            {
                var runLength = header >> 1;
                var repeated = ReadLittleEndian(ref payload, byteWidth: 1);
                var copyLength = (int)Math.Min(runLength, valueCount - valueIndex);
                if (repeated != 0)
                {
                    Array.Fill(values, repeated, (int)valueIndex, copyLength);
                    count += copyLength;
                }
                valueIndex += (uint)copyLength;
                continue;
            }

            var literalGroupCount = header >> 1;
            if (literalGroupCount > uint.MaxValue / 8)
                throw new CorruptParquetException($"Definition levels literal run group count {literalGroupCount} is too large.");
            var literalCount = literalGroupCount * 8U;
            for (var i = 0U; i < literalCount && valueIndex < valueCount; i++)
            {
                var v = ReadBitPackedValue(ref payload, bitWidth: 1, (int)i);
                values[(int)valueIndex++] = v;
                count += v;
            }

            var literalByteCount = (literalCount + 7U) >> 3;
            if (literalByteCount > (uint)payload.Length)
                throw new CorruptParquetException(
                    $"Definition level literal group claims {literalByteCount} bytes but only {payload.Length} remain.");
            payload = payload[(int)literalByteCount..];
        }

        nonNullCount = count;
        return values;
    }

    static ReadOnlyMemory<T> ExpandWithDefinitionLevels<T>(Array physicalValues, int[] definitionLevels, uint totalValueCount)
    {
        var result = new T[(int)totalValueCount];
        var valueIndex = 0;
        if (typeof(T) == typeof(int?))
        {
            var src = (int[])physicalValues;
            var dst = (int?[])(object)result;
            for (var i = 0; i < totalValueCount; i++)
                dst[i] = definitionLevels[i] != 0 ? src[valueIndex++] : null;
        }
        else if (typeof(T) == typeof(long?))
        {
            var src = (long[])physicalValues;
            var dst = (long?[])(object)result;
            for (var i = 0; i < totalValueCount; i++)
                dst[i] = definitionLevels[i] != 0 ? src[valueIndex++] : null;
        }
        else if (typeof(T) == typeof(bool?))
        {
            var src = (bool[])physicalValues;
            var dst = (bool?[])(object)result;
            for (var i = 0; i < totalValueCount; i++)
                dst[i] = definitionLevels[i] != 0 ? src[valueIndex++] : null;
        }
        else if (typeof(T) == typeof(float?))
        {
            var src = (float[])physicalValues;
            var dst = (float?[])(object)result;
            for (var i = 0; i < totalValueCount; i++)
                dst[i] = definitionLevels[i] != 0 ? src[valueIndex++] : null;
        }
        else if (typeof(T) == typeof(double?))
        {
            var src = (double[])physicalValues;
            var dst = (double?[])(object)result;
            for (var i = 0; i < totalValueCount; i++)
                dst[i] = definitionLevels[i] != 0 ? src[valueIndex++] : null;
        }
        else if (typeof(T) == typeof(byte?))
        {
            var src = (byte[])physicalValues;
            var dst = (byte?[])(object)result;
            for (var i = 0; i < totalValueCount; i++)
                dst[i] = definitionLevels[i] != 0 ? src[valueIndex++] : null;
        }
        else if (typeof(T) == typeof(ushort?))
        {
            var src = (ushort[])physicalValues;
            var dst = (ushort?[])(object)result;
            for (var i = 0; i < totalValueCount; i++)
                dst[i] = definitionLevels[i] != 0 ? src[valueIndex++] : null;
        }
        else if (typeof(T) == typeof(uint?))
        {
            var src = (uint[])physicalValues;
            var dst = (uint?[])(object)result;
            for (var i = 0; i < totalValueCount; i++)
                dst[i] = definitionLevels[i] != 0 ? src[valueIndex++] : null;
        }
        else if (typeof(T) == typeof(ulong?))
        {
            var src = (ulong[])physicalValues;
            var dst = (ulong?[])(object)result;
            for (var i = 0; i < totalValueCount; i++)
                dst[i] = definitionLevels[i] != 0 ? src[valueIndex++] : null;
        }
        else if (typeof(T) == typeof(DateOnly?))
        {
            var src = (DateOnly[])physicalValues;
            var dst = (DateOnly?[])(object)result;
            for (var i = 0; i < totalValueCount; i++)
                dst[i] = definitionLevels[i] != 0 ? src[valueIndex++] : null;
        }
        else if (typeof(T) == typeof(DateTime?))
        {
            var src = (DateTime[])physicalValues;
            var dst = (DateTime?[])(object)result;
            for (var i = 0; i < totalValueCount; i++)
                dst[i] = definitionLevels[i] != 0 ? src[valueIndex++] : null;
        }
        else if (typeof(T) == typeof(DateTimeOffset?))
        {
            var src = (DateTimeOffset[])physicalValues;
            var dst = (DateTimeOffset?[])(object)result;
            for (var i = 0; i < totalValueCount; i++)
                dst[i] = definitionLevels[i] != 0 ? src[valueIndex++] : null;
        }
        else if (typeof(T) == typeof(TimeOnly?))
        {
            var src = (TimeOnly[])physicalValues;
            var dst = (TimeOnly?[])(object)result;
            for (var i = 0; i < totalValueCount; i++)
                dst[i] = definitionLevels[i] != 0 ? src[valueIndex++] : null;
        }
        else if (typeof(T) == typeof(ReadOnlyMemory<byte>?))
        {
            var src = (ReadOnlyMemory<byte>[])physicalValues;
            var dst = (ReadOnlyMemory<byte>?[])(object)result;
            for (var i = 0; i < totalValueCount; i++)
                dst[i] = definitionLevels[i] != 0 ? src[valueIndex++] : null;
        }
        else
        {
            // Reference types such as byte[] use null as their nullable representation.
            for (var i = 0; i < totalValueCount; i++)
            {
                if (definitionLevels[i] != 0)
                    result[i] = (T)physicalValues.GetValue(valueIndex++)!;
            }
        }
        return new ReadOnlyMemory<T>(result, 0, (int)totalValueCount);
    }

    static int[] ReadRleBitPackedHybrid(ReadOnlySpan<byte> payload, uint valueCount, bool hasBitWidthPrefix)
    {
        if (payload.IsEmpty)
            return [];
        var bitWidth = hasBitWidthPrefix ? payload[0] : 0;
        if (hasBitWidthPrefix)
            payload = payload[1..];
        return ReadRleBitPackedHybrid(payload, valueCount, bitWidth);
    }

    static int[] ReadRleBitPackedHybrid(ReadOnlySpan<byte> payload, uint valueCount, int bitWidth)
    {
        var values = new int[(int)valueCount];
        var valueIndex = 0U;
        while (valueIndex < valueCount)
        {
            var header = ReadUnsignedVarInt(ref payload);
            if ((header & 1U) == 0)
            {
                var runLength = header >> 1;
                var byteWidth = (bitWidth + 7) >> 3;
                var repeated = byteWidth == 0 ? 0 : ReadLittleEndian(ref payload, byteWidth);
                var copyLength = Math.Min(runLength, valueCount - valueIndex);
                Array.Fill(values, repeated, (int)valueIndex, (int)copyLength);
                valueIndex += copyLength;
                continue;
            }

            var literalGroupCount = header >> 1;
            if (literalGroupCount > uint.MaxValue / 8)
                throw new CorruptParquetException($"RLE literal run group count {literalGroupCount} is too large.");
            var literalCount = literalGroupCount * 8U;
            for (var i = 0U; i < literalCount && valueIndex < valueCount; i++)
                values[(int)valueIndex++] = ReadBitPackedValue(ref payload, bitWidth, (int)i);

            if (bitWidth > 0 && literalCount > (uint.MaxValue - 7) / (uint)bitWidth)
                throw new CorruptParquetException($"RLE literal run bit count overflow (count={literalCount}, width={bitWidth}).");
            var literalByteCount = bitWidth == 0 ? 0U : (literalCount * (uint)bitWidth + 7U) >> 3;
            if (literalByteCount > (uint)payload.Length)
                throw new CorruptParquetException(
                    $"RLE literal group claims {literalByteCount} bytes but only {payload.Length} remain.");
            payload = payload[(int)literalByteCount..];
        }

        return values;
    }

    static void DecodeBooleanRle(ReadOnlySpan<byte> payload, Span<bool> destination)
    {
        payload = ReadBooleanRlePayload(payload);
        var valueIndex = 0U;
        var destinationLength = (uint)destination.Length;
        while (valueIndex < destinationLength)
        {
            var header = ReadUnsignedVarInt(ref payload);
            if ((header & 1U) == 0)
            {
                var runLength = header >> 1;
                var repeated = ReadLittleEndian(ref payload, 1) != 0;
                var copyLength = Math.Min(runLength, destinationLength - valueIndex);
                destination.Slice((int)valueIndex, (int)copyLength).Fill(repeated);
                valueIndex += copyLength;
                continue;
            }

            var literalGroupCount = header >> 1;
            if (literalGroupCount > uint.MaxValue / 8)
                throw new CorruptParquetException($"Boolean RLE literal run group count {literalGroupCount} is too large.");
            var literalCount = literalGroupCount * 8U;
            for (var i = 0U; i < literalCount && valueIndex < destinationLength; i++)
                destination[(int)valueIndex++] = ReadBitPackedValue(ref payload, bitWidth: 1, (int)i) != 0;

            var literalByteCount = (literalCount + 7U) >> 3;
            if (literalByteCount > (uint)payload.Length)
                throw new CorruptParquetException(
                    $"Boolean RLE literal group claims {literalByteCount} bytes but only {payload.Length} remain.");
            payload = payload[(int)literalByteCount..];
        }
    }

    static ReadOnlySpan<byte> ReadBooleanRlePayload(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < sizeof(int))
            throw new CorruptParquetException(
                $"Boolean RLE payload ({payload.Length} bytes) is too short for its length prefix.");

        var encodedLength = BinaryPrimitives.ReadInt32LittleEndian(payload);
        var remaining = payload[sizeof(int)..];
        if (encodedLength != remaining.Length)
            throw new CorruptParquetException(
                $"Boolean RLE payload declares {encodedLength} encoded bytes but contains {remaining.Length}.");
        return remaining;
    }

    static int ReadBitPackedValue(ref ReadOnlySpan<byte> payload, int bitWidth, int index)
    {
        if (bitWidth == 0)
            return 0;

        var bitOffset = index * bitWidth;
        var byteIndex = bitOffset >> 3;
        var shift = bitOffset & 7;
        if (byteIndex >= payload.Length)
            throw new CorruptParquetException(
                $"Bit-packed value at bit offset {bitOffset} reads past end of payload ({payload.Length} bytes).");
        ulong bits = payload[byteIndex];
        if (byteIndex + 1 < payload.Length)
            bits |= (ulong)payload[byteIndex + 1] << 8;
        if (byteIndex + 2 < payload.Length)
            bits |= (ulong)payload[byteIndex + 2] << 16;
        if (byteIndex + 3 < payload.Length)
            bits |= (ulong)payload[byteIndex + 3] << 24;
        bits >>= shift;
        var mask = (1UL << bitWidth) - 1UL;
        return (int)(bits & mask);
    }

    static uint ReadUnsignedVarInt(ref ReadOnlySpan<byte> payload)
    {
        uint value = 0;
        var shift = 0;
        while (true)
        {
            if (payload.IsEmpty)
                throw new CorruptParquetException("Unexpected end of RLE/bit-pack payload while reading varint.");
            var b = payload[0];
            payload = payload[1..];
            value |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
                return value;
            shift += 7;
        }
    }

    static int ReadLittleEndian(ref ReadOnlySpan<byte> payload, int byteWidth)
    {
        if (byteWidth > payload.Length)
            throw new CorruptParquetException(
                $"RLE run needs {byteWidth} bytes but only {payload.Length} remain.");
        var value = byteWidth switch
        {
            1 => payload[0],
            2 => BinaryPrimitives.ReadUInt16LittleEndian(payload),
            3 => payload[0] | (payload[1] << 8) | (payload[2] << 16),
            4 => unchecked((int)BinaryPrimitives.ReadUInt32LittleEndian(payload)),
            _ => throw new CorruptParquetException($"Unsupported RLE byte width '{byteWidth}'.")
        };
        payload = payload[byteWidth..];
        return value;
    }

}
