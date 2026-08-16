using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Plank.Schema;

namespace Plank.Reading.Logical.Internal;

static class ColumnChunkReader
{
    internal const int DecodeBatchSizeBytes = 256 * 1024;

    static readonly DateOnly UnixEpochDate = new(1970, 1, 1);
    static readonly ulong[] ExpandedDefinitionBytes = CreateExpandedDefinitionBytes();
    static readonly byte[] DefinitionByteCounts = CreateDefinitionByteCounts();
    internal static bool TryDecodeDictionaryPageIntoNative<T>(PageHeader header, ReadOnlySpan<byte> payload,
        Column column, ref ColumnReadBuffers<T> state, IParquetBufferPool bufferPool)
    {
        if (header.Type != PageHeaderType.DictionaryPage)
            return false;

        if (typeof(T) == typeof(BinaryValueDescriptor))
            return TryDecodeBinaryDictionaryPage(header, payload, column, ref state, bufferPool);
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            return false;

        var physicalType = GetPhysicalDecodeType<T>(column);
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
        if (physicalType == typeof(decimal))
            return TryDecodeDictionaryIntoNative<T, decimal>(header, payload, column, ref state, bufferPool);
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
        if (physicalType == typeof(Guid))
            return TryDecodeDictionaryIntoNative<T, Guid>(header, payload, column, ref state, bufferPool);
        return false;
    }

    static bool TryDecodeDictionaryIntoNative<TPage, TValue>(PageHeader header, ReadOnlySpan<byte> payload,
        Column column, ref ColumnReadBuffers<TPage> state, IParquetBufferPool bufferPool)
    {
        // Plain encoding spends at least one bit per value — booleans are
        // bit-packed, every other physical type is at least a byte wide — so the
        // payload size caps how many dictionary entries can exist. The header's
        // count used to go straight into the buffer sizing below, where a
        // corrupt value overflowed and escaped as OverflowException instead of
        // CorruptParquetException.
        var maximumValueCount = Math.Min((ulong)payload.Length * 8, int.MaxValue);
        if (header.ValueCount > maximumValueCount)
            throw new CorruptParquetException(
                $"Dictionary page declares {header.ValueCount} values, but its {payload.Length}-byte payload cannot encode that many.");

        var valueCount = (int)header.ValueCount;
        var destination = state.GetDictionary<TValue>(valueCount, bufferPool);
        return TryDecodeValuesIntoNative(payload, column, header.ValueCount, header.Encoding, destination);
    }

    internal static bool TryDecodeNestedPageIntoNative<T>(PageHeader header, ReadOnlySpan<byte> payload,
        LeafColumn definition, ref ColumnReadBuffers<T> state, IParquetBufferPool bufferPool,
        out NestedColumnBuffer<T> buffer)
    {
        buffer = default;
        if (header.Type is not (PageHeaderType.DataPage or PageHeaderType.DataPageV2) ||
            RuntimeHelpers.IsReferenceOrContainsReferences<T>() || GetPhysicalDecodeType<T>() != typeof(T))
            return false;

        var column = definition.Column;
        var isBinary = typeof(T) == typeof(BinaryValueDescriptor);
        if (isBinary != IsBinaryPhysicalType(column.PhysicalType))
            return false;

        var maxRepetitionLevel = definition.MaxRepetitionLevel;
        var maxDefinitionLevel = definition.MaxDefinitionLevel;
        if (maxRepetitionLevel < 0 || maxDefinitionLevel < 0)
            throw new CorruptParquetException($"Column '{column.Name}' has negative maximum levels.");

        var levelCount = PageValueCount(header, "levels");
        state.GetLevels(levelCount, bufferPool, out var repetitionLevels, out var definitionLevels);

        ReadOnlySpan<byte> dataPayload;
        if (header.Type == PageHeaderType.DataPageV2)
        {
            if (header.NullCount > header.ValueCount)
                throw new CorruptParquetException(
                    $"Page null count ({header.NullCount}) exceeds value count ({header.ValueCount}).");
            var repetitionByteLength = checked((int)header.RepetitionLevelsByteLength);
            var definitionByteLength = checked((int)header.DefinitionLevelsByteLength);
            var levelByteLength = checked(repetitionByteLength + definitionByteLength);
            if ((uint)levelByteLength > (uint)payload.Length)
                throw new CorruptParquetException(
                    $"Level bytes ({levelByteLength}) exceed page payload size ({payload.Length}).");

            DecodeLevels(payload[..repetitionByteLength], repetitionLevels, EncodingKind.Rle,
                maxRepetitionLevel, "repetition");
            DecodeLevels(payload.Slice(repetitionByteLength, definitionByteLength), definitionLevels,
                EncodingKind.Rle, maxDefinitionLevel, "definition");
            dataPayload = payload[levelByteLength..];
        }
        else
        {
            var remaining = payload;
            DecodeDataPageV1Levels(ref remaining, header.ValueCount, header.RepetitionLevelEncoding,
                maxRepetitionLevel, "repetition", repetitionLevels);
            DecodeDataPageV1Levels(ref remaining, header.ValueCount, header.DefinitionLevelEncoding,
                maxDefinitionLevel, "definition", definitionLevels);
            dataPayload = remaining;
        }

        var physicalCount = 0;
        var rowCount = 0;
        for (var i = 0; i < levelCount; i++)
        {
            if (definitionLevels[i] == maxDefinitionLevel)
                physicalCount++;
            if (repetitionLevels[i] == 0)
                rowCount++;
        }

        if (header.Type == PageHeaderType.DataPageV2 && header.NullCount != levelCount - physicalCount)
            throw new CorruptParquetException(
                $"Definition levels contain {levelCount - physicalCount} null entries, expected {header.NullCount}.");

        if (isBinary)
        {
            var scratch = MemoryMarshal.Cast<byte, int>(
                state.GetScratch(checked(physicalCount * 2 * sizeof(int)), bufferPool));
            if (!TryDecodeBinaryValues(dataPayload, [], physicalCount, physicalCount, column,
                    header.Encoding, scratch, ref state, bufferPool))
                return false;
        }
        else
        {
            var destination = state.GetValues<T>(physicalCount, bufferPool);
            if (header.Encoding is EncodingKind.RleDictionary or EncodingKind.PlainDictionary)
            {
                if (!state.HasDictionary)
                    return false;
                DecodeDictionaryIndexesIntoBuffer(dataPayload, checked((uint)physicalCount),
                    state.GetDictionary<T>(), destination);
            }
            else if (!TryDecodeValuesIntoNative(dataPayload, column, checked((uint)physicalCount),
                         header.Encoding, destination))
                return false;
        }

        buffer = new NestedColumnBuffer<T>(state.CreateNativeBuffer(physicalCount), state.Levels, levelCount,
            rowCount, levelCount != 0 && repetitionLevels[0] != 0, maxRepetitionLevel, maxDefinitionLevel);
        return true;
    }

    static void DecodeDataPageV1Levels(ref ReadOnlySpan<byte> payload, uint valueCount, EncodingKind encoding,
        int maxLevel, string levelName, Span<int> destination)
    {
        if (maxLevel == 0)
        {
            destination.Clear();
            return;
        }

        var bitWidth = GetLevelBitWidth(maxLevel);
        var byteLength = GetDataPageV1LevelPayloadLength(payload, valueCount, encoding, bitWidth,
            levelName, out var offset);
        DecodeLevels(payload.Slice(offset, byteLength), destination, encoding, maxLevel, levelName);
        payload = payload[(offset + byteLength)..];
    }

    static void DecodeLevels(ReadOnlySpan<byte> payload, Span<int> destination, EncodingKind encoding,
        int maxLevel, string levelName)
    {
        if (destination.IsEmpty)
            return;
        var bitWidth = GetLevelBitWidth(maxLevel);
        if (bitWidth == 0)
            destination.Clear();
        else if (encoding == EncodingKind.Rle)
            DecodeRleBitPackedLevels(payload, bitWidth, destination, levelName);
        else if (encoding == EncodingKind.BitPacked)
            LegacyBitPackedDecoder.Decode(payload, bitWidth, destination);
        else
            throw new NotSupportedException($"{levelName} level encoding '{encoding}' is not supported.");

        for (var i = 0; i < destination.Length; i++)
            if ((uint)destination[i] > (uint)maxLevel)
                throw new CorruptParquetException(
                    $"{levelName} level {destination[i]} exceeds the schema maximum of {maxLevel}.");
    }

    static void DecodeRleBitPackedLevels(ReadOnlySpan<byte> payload, int bitWidth, Span<int> destination,
        string levelName)
    {
        var valueIndex = 0;
        while (valueIndex < destination.Length)
        {
            var header = ReadUnsignedVarInt(ref payload);
            if ((header & 1U) == 0)
            {
                var runLength = header >> 1;
                if (runLength == 0)
                    throw new CorruptParquetException($"{levelName} levels contain an empty RLE run.");
                var byteWidth = (bitWidth + 7) >> 3;
                var repeated = ReadLittleEndian(ref payload, byteWidth);
                var copyLength = (int)Math.Min(runLength, checked((uint)(destination.Length - valueIndex)));
                destination.Slice(valueIndex, copyLength).Fill(repeated);
                valueIndex += copyLength;
                continue;
            }

            var literalGroupCount = header >> 1;
            if (literalGroupCount == 0)
                throw new CorruptParquetException($"{levelName} levels contain an empty bit-packed run.");
            if (literalGroupCount > uint.MaxValue / 8)
                throw new CorruptParquetException(
                    $"{levelName} level literal run group count {literalGroupCount} is too large.");
            var literalCount = literalGroupCount * 8U;
            if (literalCount > (uint.MaxValue - 7) / checked((uint)bitWidth))
                throw new CorruptParquetException(
                    $"{levelName} level literal run bit count overflows its supported range.");
            var literalByteCount = (literalCount * checked((uint)bitWidth) + 7U) >> 3;
            if (literalByteCount > (uint)payload.Length)
                throw new CorruptParquetException(
                    $"{levelName} level literal run claims {literalByteCount} bytes but only {payload.Length} remain.");

            var valuesToCopy = Math.Min(literalCount, checked((uint)(destination.Length - valueIndex)));
            for (var i = 0U; i < valuesToCopy; i++)
                destination[valueIndex++] = ReadBitPackedValue(ref payload, bitWidth, checked((int)i));
            payload = payload[checked((int)literalByteCount)..];
        }
    }

    static int GetLevelBitWidth(int maxLevel)
    {
        var bitWidth = 0;
        while (maxLevel != 0)
        {
            bitWidth++;
            maxLevel >>= 1;
        }
        return bitWidth;
    }

    internal enum FixedWidthDecoderKind : byte
    {
        None,
        Plain,
        ByteStreamSplit,
        Dictionary
    }

    internal struct FixedWidthPageState
    {
        internal int ValueCount;
        internal int ValueOffset;
        internal int PhysicalCount;
        internal int PhysicalOffset;
        internal int DataOffset;
        internal int DataLength;
        internal int DefinitionBitsetLength;
        internal int BatchElementSize;
        internal FixedWidthDecoderKind DecoderKind;
        internal bool IsNullable;

        internal readonly bool Active
            => ValueOffset < ValueCount;
    }

    internal static bool CanBatchDictionaryPages<T>(Column column)
    {
        var converter = column.Converter;
        return converter is null
            ? GetPhysicalDecodeType<T>(column) != typeof(T)
            : converter.IsNullableValueType(typeof(T));
    }

    internal static bool TryStartFixedWidthPageBatches<T>(PageHeader header, ReadOnlySpan<byte> payload,
        Column column, ulong rowCount, ref ColumnReadBuffers<T> buffers, IParquetBufferPool bufferPool,
        ref FixedWidthPageState page, out ColumnBuffer<T> buffer)
    {
        buffer = default;
        page = default;
        var physicalType = GetPhysicalDecodeType<T>(column);
        var converter = column.Converter;
        var isNullable = converter is null
            ? physicalType != typeof(T)
            : converter.IsNullableValueType(typeof(T));
        var isRequired = converter is null
            ? physicalType == typeof(T)
            : converter.SupportsValueType(typeof(T)) && !isNullable;
        if (header.Type is not (PageHeaderType.DataPage or PageHeaderType.DataPageV2) ||
            header.Encoding is not (EncodingKind.Plain or EncodingKind.ByteStreamSplit or
                EncodingKind.RleDictionary or EncodingKind.PlainDictionary) ||
            column.Options.Repetition == ParquetRepetition.Repeated ||
            RuntimeHelpers.IsReferenceOrContainsReferences<T>() ||
            (!isNullable && !isRequired) ||
            (column.Options.Repetition == ParquetRepetition.Optional && !isNullable) ||
            !CanBatchFixedWidthProjection(column, physicalType, header.Encoding))
            return false;
        // The required dictionary decoder maps indexes directly into the caller's output. Materializing
        // dictionary values for resumable batches measured slower in both single- and multi-threaded reads.
        if (isRequired && header.Encoding is
            (EncodingKind.RleDictionary or EncodingKind.PlainDictionary))
            return false;
        if (header.ValueCount == 0)
            return false;
        if (header.ValueCount > rowCount)
            throw new CorruptParquetException(
                $"Page value count ({header.ValueCount}) exceeds row group row count ({rowCount}).");
        var decoderKind = header.Encoding switch
        {
            EncodingKind.Plain => FixedWidthDecoderKind.Plain,
            EncodingKind.ByteStreamSplit => FixedWidthDecoderKind.ByteStreamSplit,
            EncodingKind.RleDictionary or EncodingKind.PlainDictionary => FixedWidthDecoderKind.Dictionary,
            _ => FixedWidthDecoderKind.None
        };

        var definitionOffset = 0;
        var definitionLength = 0;
        var dataOffset = 0;
        var expectedPhysicalCount = header.ValueCount;
        var definitionEncoding = header.Type == PageHeaderType.DataPage
            ? header.DefinitionLevelEncoding
            : EncodingKind.Rle;
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
            definitionOffset = checked((int)header.RepetitionLevelsByteLength);
            definitionLength = checked((int)header.DefinitionLevelsByteLength);
            dataOffset = levelBytes;
            expectedPhysicalCount -= header.NullCount;
        }
        else if (column.Options.Repetition == ParquetRepetition.Optional)
        {
            definitionLength = GetDataPageV1LevelPayloadLength(payload, header.ValueCount,
                definitionEncoding, bitWidth: 1, "definition", out definitionOffset);
            dataOffset = checked(definitionOffset + definitionLength);
        }

        var valueCount = PageValueCount(header, "values");
        var definitionBitsetLength = 0;
        int physicalCount;
        if (definitionLength == 0)
            physicalCount = valueCount;
        else
        {
            definitionBitsetLength = checked((valueCount + 7) / 8);
            DecodeDefinitionBitset(payload.Slice(definitionOffset, definitionLength), valueCount,
                definitionEncoding, buffers.GetCompactDefinitions(definitionBitsetLength, bufferPool),
                out physicalCount);
        }
        if (header.Type == PageHeaderType.DataPageV2 && physicalCount != expectedPhysicalCount)
            throw new CorruptParquetException(
                $"Definition levels contain {physicalCount} values, expected {expectedPhysicalCount}.");

        var dataPayload = payload[dataOffset..];
        var physicalSize = GetEncodedFixedWidth(column);
        if (header.Encoding == EncodingKind.Plain)
            ValidateFixedWidthPlainPayload(dataPayload, column, physicalCount, physicalSize);
        else if (header.Encoding == EncodingKind.ByteStreamSplit &&
                 (long)physicalCount * physicalSize > dataPayload.Length)
            throw new CorruptParquetException(
                $"ByteStreamSplit payload ({dataPayload.Length} bytes) is too short for " +
                $"{physicalCount} {physicalSize}-byte values.");
        else if (header.Encoding is EncodingKind.RleDictionary or EncodingKind.PlainDictionary)
        {
            if (!buffers.HasDictionary)
                return false;
            if (physicalCount != 0 && dataPayload.IsEmpty)
                throw new CorruptParquetException(
                    "Dictionary payload is empty but value count is non-zero.");
            if (!dataPayload.IsEmpty && dataPayload[0] > 32)
                throw new CorruptParquetException(
                    $"Dictionary bit width {dataPayload[0]} exceeds the maximum of 32.");
            MaterializeFixedWidthDictionaryValues(dataPayload, physicalType, physicalCount,
                ref buffers, bufferPool);
        }

        page = new FixedWidthPageState
        {
            ValueCount = valueCount,
            PhysicalCount = physicalCount,
            DataOffset = dataOffset,
            DataLength = payload.Length - dataOffset,
            DefinitionBitsetLength = definitionBitsetLength,
            BatchElementSize = Math.Max(
                Math.Max(Unsafe.SizeOf<T>(), GetDecodedFixedWidthSize(physicalType)),
                Math.Max(GetEncodedFixedWidth(column),
                    isNullable && converter is not null ? sizeof(int) : 1)),
            DecoderKind = decoderKind,
            IsNullable = isNullable
        };
        buffer = DecodeNextFixedWidthBatch(payload, column, ref buffers, bufferPool, ref page);
        return true;
    }

    internal static ColumnBuffer<T> DecodeNextFixedWidthBatch<T>(ReadOnlySpan<byte> payload, Column column,
        ref ColumnReadBuffers<T> buffers, IParquetBufferPool bufferPool, ref FixedWidthPageState page)
    {
        System.Diagnostics.Debug.Assert(page.Active);
        System.Diagnostics.Debug.Assert(page.DecoderKind != FixedWidthDecoderKind.None);
        System.Diagnostics.Debug.Assert(page.DataOffset >= 0 && page.DataLength >= 0 &&
            page.DataOffset <= payload.Length - page.DataLength);

        var batchCapacity = Math.Max(1, DecodeBatchSizeBytes / page.BatchElementSize);
        var batchCount = Math.Min(batchCapacity, page.ValueCount - page.ValueOffset);
        var values = buffers.GetValues<T>(batchCount, bufferPool);
        int physicalBatchCount;
        ReadOnlySpan<byte> byteDefinitions = [];
        ReadOnlySpan<int> intDefinitions = [];
        if (!page.IsNullable)
            physicalBatchCount = batchCount;
        else if (column.Converter is not null)
        {
            var definitions = buffers.GetExpandedDefinitions(batchCount, bufferPool);
            if (page.DefinitionBitsetLength == 0)
            {
                definitions.Fill(1);
                physicalBatchCount = batchCount;
            }
            else
                ExpandDefinitionBitset(buffers.GetCompactDefinitions(page.DefinitionBitsetLength),
                    page.ValueOffset, definitions, out physicalBatchCount);
            intDefinitions = definitions;
        }
        else
        {
            var definitions = AsBytes(values)[..batchCount];
            if (page.DefinitionBitsetLength == 0)
            {
                definitions.Fill(1);
                physicalBatchCount = batchCount;
            }
            else
                ExpandDefinitionBitset(buffers.GetCompactDefinitions(page.DefinitionBitsetLength),
                    page.ValueOffset, definitions, out physicalBatchCount);
            byteDefinitions = definitions;
        }
        if (physicalBatchCount > page.PhysicalCount - page.PhysicalOffset)
            throw new CorruptParquetException(
                $"Definition levels contain more than the expected {page.PhysicalCount} physical values.");
        var dataPayload = payload.Slice(page.DataOffset, page.DataLength);
        DecodeFixedWidthBatch(dataPayload, column, byteDefinitions, intDefinitions,
            physicalBatchCount, values, ref page, ref buffers, bufferPool);

        page.ValueOffset += batchCount;
        page.PhysicalOffset += physicalBatchCount;
        if (!page.Active && page.PhysicalOffset != page.PhysicalCount)
            throw new CorruptParquetException(
                $"Definition levels consumed {page.PhysicalOffset} physical values, " +
                $"expected {page.PhysicalCount}.");
        return buffers.CreateNativeBuffer(batchCount);
    }

    static bool CanBatchFixedWidthProjection(Column column, Type physicalType, EncodingKind encoding)
    {
        if (physicalType != typeof(int) && physicalType != typeof(long) && physicalType != typeof(bool) &&
            physicalType != typeof(float) && physicalType != typeof(double) && physicalType != typeof(decimal) &&
            physicalType != typeof(byte) && physicalType != typeof(ushort) && physicalType != typeof(uint) &&
            physicalType != typeof(ulong) && physicalType != typeof(DateOnly) &&
            physicalType != typeof(DateTime) && physicalType != typeof(DateTimeOffset) &&
            physicalType != typeof(TimeOnly) && physicalType != typeof(Guid))
            return false;
        if (encoding is EncodingKind.RleDictionary or EncodingKind.PlainDictionary)
            return true;

        return column.PhysicalType switch
        {
            ParquetPhysicalType.Boolean => physicalType == typeof(bool) && encoding == EncodingKind.Plain,
            ParquetPhysicalType.Int32 => physicalType == typeof(int) || physicalType == typeof(byte) ||
                physicalType == typeof(ushort) || physicalType == typeof(uint) || physicalType == typeof(decimal) ||
                physicalType == typeof(DateOnly) || physicalType == typeof(TimeOnly),
            ParquetPhysicalType.Int64 => physicalType == typeof(long) || physicalType == typeof(ulong) ||
                physicalType == typeof(decimal) || physicalType == typeof(TimeOnly) ||
                physicalType == typeof(DateTime) || physicalType == typeof(DateTimeOffset),
            ParquetPhysicalType.Float => physicalType == typeof(float),
            ParquetPhysicalType.Double => physicalType == typeof(double),
            ParquetPhysicalType.FixedLenByteArray => physicalType == typeof(Guid) || physicalType == typeof(decimal),
            _ => false
        };
    }

    static int GetEncodedFixedWidth(Column column)
        => column.PhysicalType switch
        {
            ParquetPhysicalType.Boolean => 1,
            ParquetPhysicalType.Int32 or ParquetPhysicalType.Float => sizeof(int),
            ParquetPhysicalType.Int64 or ParquetPhysicalType.Double => sizeof(long),
            ParquetPhysicalType.FixedLenByteArray => GetFixedBinaryLength(column),
            _ => throw new InvalidOperationException(
                $"Physical type '{column.PhysicalType}' is not fixed-width for batched decoding.")
        };

    static int GetDecodedFixedWidthSize(Type type)
        => type == typeof(bool) || type == typeof(byte)
            ? sizeof(byte)
            : type == typeof(ushort)
                ? sizeof(ushort)
                : type == typeof(int) || type == typeof(float) || type == typeof(uint) ||
                  type == typeof(DateOnly)
                    ? sizeof(int)
                    : type == typeof(long) || type == typeof(double) || type == typeof(ulong) ||
                      type == typeof(DateTime) || type == typeof(TimeOnly)
                        ? sizeof(long)
                        : type == typeof(decimal) || type == typeof(DateTimeOffset) || type == typeof(Guid)
                            ? 16
                            : throw new InvalidOperationException(
                                $"Unsupported fixed-width decoded type '{type}'.");

    static void ValidateFixedWidthPlainPayload(ReadOnlySpan<byte> payload, Column column,
        int physicalCount, int physicalSize)
    {
        if (column.PhysicalType == ParquetPhysicalType.Boolean)
        {
            var byteCount = checked((physicalCount + 7) / 8);
            if (payload.Length < byteCount)
                throw new CorruptParquetException(
                    $"Payload ({payload.Length} bytes) is too short to decode {physicalCount} plain boolean values.");
            return;
        }
        ValidatePlainPayload(payload, checked((uint)physicalCount), checked((uint)physicalSize));
    }

    static void MaterializeFixedWidthDictionaryValues<T>(ReadOnlySpan<byte> payload, Type physicalType,
        int physicalCount, ref ColumnReadBuffers<T> buffers, IParquetBufferPool bufferPool)
    {
        if (physicalType == typeof(int))
            MaterializeFixedWidthDictionaryValues<T, int>(payload, physicalCount, ref buffers, bufferPool);
        else if (physicalType == typeof(long))
            MaterializeFixedWidthDictionaryValues<T, long>(payload, physicalCount, ref buffers, bufferPool);
        else if (physicalType == typeof(bool))
            MaterializeFixedWidthDictionaryValues<T, bool>(payload, physicalCount, ref buffers, bufferPool);
        else if (physicalType == typeof(float))
            MaterializeFixedWidthDictionaryValues<T, float>(payload, physicalCount, ref buffers, bufferPool);
        else if (physicalType == typeof(double))
            MaterializeFixedWidthDictionaryValues<T, double>(payload, physicalCount, ref buffers, bufferPool);
        else if (physicalType == typeof(decimal))
            MaterializeFixedWidthDictionaryValues<T, decimal>(payload, physicalCount, ref buffers, bufferPool);
        else if (physicalType == typeof(byte))
            MaterializeFixedWidthDictionaryValues<T, byte>(payload, physicalCount, ref buffers, bufferPool);
        else if (physicalType == typeof(ushort))
            MaterializeFixedWidthDictionaryValues<T, ushort>(payload, physicalCount, ref buffers, bufferPool);
        else if (physicalType == typeof(uint))
            MaterializeFixedWidthDictionaryValues<T, uint>(payload, physicalCount, ref buffers, bufferPool);
        else if (physicalType == typeof(ulong))
            MaterializeFixedWidthDictionaryValues<T, ulong>(payload, physicalCount, ref buffers, bufferPool);
        else if (physicalType == typeof(DateOnly))
            MaterializeFixedWidthDictionaryValues<T, DateOnly>(payload, physicalCount, ref buffers, bufferPool);
        else if (physicalType == typeof(DateTime))
            MaterializeFixedWidthDictionaryValues<T, DateTime>(payload, physicalCount, ref buffers, bufferPool);
        else if (physicalType == typeof(DateTimeOffset))
            MaterializeFixedWidthDictionaryValues<T, DateTimeOffset>(payload, physicalCount, ref buffers, bufferPool);
        else if (physicalType == typeof(TimeOnly))
            MaterializeFixedWidthDictionaryValues<T, TimeOnly>(payload, physicalCount, ref buffers, bufferPool);
        else if (physicalType == typeof(Guid))
            MaterializeFixedWidthDictionaryValues<T, Guid>(payload, physicalCount, ref buffers, bufferPool);
        else
            throw new InvalidOperationException($"Unsupported fixed-width physical type '{physicalType}'.");
    }

    static void MaterializeFixedWidthDictionaryValues<T, TValue>(ReadOnlySpan<byte> payload,
        int physicalCount, ref ColumnReadBuffers<T> buffers, IParquetBufferPool bufferPool)
        where TValue : struct
    {
        var physical = MemoryMarshal.Cast<byte, TValue>(buffers.GetScratch(
            checked(physicalCount * Unsafe.SizeOf<TValue>()), bufferPool));
        DecodeDictionaryIndexesIntoBuffer(payload, checked((uint)physicalCount),
            buffers.GetDictionary<TValue>(), physical);
    }

    static void DecodeFixedWidthBatch<T>(ReadOnlySpan<byte> payload, Column column,
        ReadOnlySpan<byte> byteDefinitions, ReadOnlySpan<int> intDefinitions,
        int physicalBatchCount, Span<T> values, ref FixedWidthPageState page, ref ColumnReadBuffers<T> buffers,
        IParquetBufferPool bufferPool)
    {
        var physicalType = GetPhysicalDecodeType<T>(column);
        var totalPhysicalCount = page.PhysicalCount;
        var physicalOffset = page.PhysicalOffset;
        var decoderKind = page.DecoderKind;
        if (physicalType == typeof(int))
            DecodeFixedWidthBatch<T, int>(payload, column, byteDefinitions, intDefinitions,
                totalPhysicalCount, physicalOffset, physicalBatchCount, decoderKind, values,
                ref buffers, bufferPool);
        else if (physicalType == typeof(long))
            DecodeFixedWidthBatch<T, long>(payload, column, byteDefinitions, intDefinitions,
                totalPhysicalCount, physicalOffset, physicalBatchCount, decoderKind, values,
                ref buffers, bufferPool);
        else if (physicalType == typeof(bool))
            DecodeFixedWidthBatch<T, bool>(payload, column, byteDefinitions, intDefinitions,
                totalPhysicalCount, physicalOffset, physicalBatchCount, decoderKind, values,
                ref buffers, bufferPool);
        else if (physicalType == typeof(float))
            DecodeFixedWidthBatch<T, float>(payload, column, byteDefinitions, intDefinitions,
                totalPhysicalCount, physicalOffset, physicalBatchCount, decoderKind, values,
                ref buffers, bufferPool);
        else if (physicalType == typeof(double))
            DecodeFixedWidthBatch<T, double>(payload, column, byteDefinitions, intDefinitions,
                totalPhysicalCount, physicalOffset, physicalBatchCount, decoderKind, values,
                ref buffers, bufferPool);
        else if (physicalType == typeof(decimal))
            DecodeFixedWidthBatch<T, decimal>(payload, column, byteDefinitions, intDefinitions,
                totalPhysicalCount, physicalOffset, physicalBatchCount, decoderKind, values,
                ref buffers, bufferPool);
        else if (physicalType == typeof(byte))
            DecodeFixedWidthBatch<T, byte>(payload, column, byteDefinitions, intDefinitions,
                totalPhysicalCount, physicalOffset, physicalBatchCount, decoderKind, values,
                ref buffers, bufferPool);
        else if (physicalType == typeof(ushort))
            DecodeFixedWidthBatch<T, ushort>(payload, column, byteDefinitions, intDefinitions,
                totalPhysicalCount, physicalOffset, physicalBatchCount, decoderKind, values,
                ref buffers, bufferPool);
        else if (physicalType == typeof(uint))
            DecodeFixedWidthBatch<T, uint>(payload, column, byteDefinitions, intDefinitions,
                totalPhysicalCount, physicalOffset, physicalBatchCount, decoderKind, values,
                ref buffers, bufferPool);
        else if (physicalType == typeof(ulong))
            DecodeFixedWidthBatch<T, ulong>(payload, column, byteDefinitions, intDefinitions,
                totalPhysicalCount, physicalOffset, physicalBatchCount, decoderKind, values,
                ref buffers, bufferPool);
        else if (physicalType == typeof(DateOnly))
            DecodeFixedWidthBatch<T, DateOnly>(payload, column, byteDefinitions, intDefinitions,
                totalPhysicalCount, physicalOffset, physicalBatchCount, decoderKind, values,
                ref buffers, bufferPool);
        else if (physicalType == typeof(DateTime))
            DecodeFixedWidthBatch<T, DateTime>(payload, column, byteDefinitions, intDefinitions,
                totalPhysicalCount, physicalOffset, physicalBatchCount, decoderKind, values,
                ref buffers, bufferPool);
        else if (physicalType == typeof(DateTimeOffset))
            DecodeFixedWidthBatch<T, DateTimeOffset>(payload, column, byteDefinitions, intDefinitions,
                totalPhysicalCount, physicalOffset, physicalBatchCount, decoderKind, values,
                ref buffers, bufferPool);
        else if (physicalType == typeof(TimeOnly))
            DecodeFixedWidthBatch<T, TimeOnly>(payload, column, byteDefinitions, intDefinitions,
                totalPhysicalCount, physicalOffset, physicalBatchCount, decoderKind, values,
                ref buffers, bufferPool);
        else if (physicalType == typeof(Guid))
            DecodeFixedWidthBatch<T, Guid>(payload, column, byteDefinitions, intDefinitions,
                totalPhysicalCount, physicalOffset, physicalBatchCount, decoderKind, values,
                ref buffers, bufferPool);
        else
            throw new InvalidOperationException($"Unsupported fixed-width projection '{typeof(T)}'.");
    }

    static void DecodeFixedWidthBatch<T, TValue>(ReadOnlySpan<byte> payload, Column column,
        ReadOnlySpan<byte> byteDefinitions, ReadOnlySpan<int> intDefinitions,
        int totalPhysicalCount, int physicalOffset, int physicalBatchCount,
        FixedWidthDecoderKind decoderKind, Span<T> values, ref ColumnReadBuffers<T> buffers,
        IParquetBufferPool bufferPool)
        where TValue : struct
    {
        var converter = column.Converter;
        if (converter is null && typeof(T) == typeof(TValue))
        {
            DecodeFixedWidthValuesInto(payload, column, totalPhysicalCount, physicalOffset,
                decoderKind, Unsafe.As<Span<T>, Span<TValue>>(ref values), ref buffers);
            return;
        }

        var physical = GetFixedWidthBatchValues<T, TValue>(payload, column, totalPhysicalCount,
            physicalOffset, physicalBatchCount, decoderKind, ref buffers, bufferPool);
        if (converter is not null)
        {
            if (intDefinitions.IsEmpty)
                converter.ConvertFromPhysical(MemoryMarshal.AsBytes(physical), AsBytes(values), physicalBatchCount);
            else
                converter.ConvertNullableFromPhysical(MemoryMarshal.AsBytes(physical), intDefinitions,
                    AsBytes(values), physicalBatchCount);
            return;
        }

        var destination = Unsafe.As<Span<T>, Span<TValue?>>(ref values);
        ScatterNullableFixedWidthBatch(byteDefinitions, physical, destination);
    }

    static ReadOnlySpan<TValue> GetFixedWidthBatchValues<T, TValue>(ReadOnlySpan<byte> payload,
        Column column, int totalPhysicalCount, int physicalOffset, int physicalBatchCount,
        FixedWidthDecoderKind decoderKind, ref ColumnReadBuffers<T> buffers,
        IParquetBufferPool bufferPool)
        where TValue : struct
    {
        if (physicalBatchCount == 0)
            return [];
        if (decoderKind == FixedWidthDecoderKind.Dictionary)
            return MemoryMarshal.Cast<byte, TValue>(buffers.GetScratch(
                    checked(totalPhysicalCount * Unsafe.SizeOf<TValue>()), bufferPool))
                .Slice(physicalOffset, physicalBatchCount);

        var physicalByteLength = checked(physicalBatchCount * Unsafe.SizeOf<TValue>());
        var scratch = buffers.GetScratch(physicalByteLength, bufferPool);
        var physical = MemoryMarshal.Cast<byte, TValue>(scratch[..physicalByteLength]);
        DecodeFixedWidthValuesInto(payload, column, totalPhysicalCount, physicalOffset,
            decoderKind, physical, ref buffers);
        return physical;
    }

    static void DecodeFixedWidthValuesInto<T, TValue>(ReadOnlySpan<byte> payload, Column column,
        int totalPhysicalCount, int physicalOffset, FixedWidthDecoderKind decoderKind,
        Span<TValue> destination, ref ColumnReadBuffers<T> buffers)
        where TValue : struct
    {
        if (destination.IsEmpty)
            return;
        if (decoderKind == FixedWidthDecoderKind.Dictionary)
        {
            MemoryMarshal.Cast<byte, TValue>(buffers.Scratch.Span)
                .Slice(physicalOffset, destination.Length).CopyTo(destination);
            return;
        }
        if (decoderKind == FixedWidthDecoderKind.Plain)
        {
            if (column.PhysicalType == ParquetPhysicalType.Boolean)
            {
                var booleans = Unsafe.As<Span<TValue>, Span<bool>>(ref destination);
                for (var i = 0; i < booleans.Length; i++)
                {
                    var bitIndex = checked(physicalOffset + i);
                    booleans[i] = ((payload[bitIndex >> 3] >> (bitIndex & 7)) & 1) != 0;
                }
                return;
            }
            var offset = checked(physicalOffset * GetEncodedFixedWidth(column));
            if (!TryDecodePlainIntoNative(payload[offset..], column,
                    checked((uint)destination.Length), destination))
                throw new InvalidOperationException(
                    $"Plain decoding declined fixed-width type '{typeof(TValue)}'.");
            return;
        }
        if (decoderKind == FixedWidthDecoderKind.ByteStreamSplit)
        {
            if (!TryDecodeByteStreamSplitSliceIntoNative(payload, column, totalPhysicalCount,
                    physicalOffset, destination))
                throw new InvalidOperationException(
                    $"ByteStreamSplit decoding declined fixed-width type '{typeof(TValue)}'.");
            return;
        }
        throw new InvalidOperationException($"Unsupported fixed-width decoder kind '{decoderKind}'.");
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    static void ScatterNullableFixedWidthBatch<TValue>(ReadOnlySpan<byte> definitions,
        ReadOnlySpan<TValue> physical, Span<TValue?> destination)
        where TValue : struct
    {
        var physicalIndex = physical.Length;
        for (var i = definitions.Length - 1; i >= 0; i--)
            destination[i] = definitions[i] == 0 ? null : physical[--physicalIndex];
        if (physicalIndex != 0)
            throw new CorruptParquetException(
                $"Definition levels consumed {physical.Length - physicalIndex} physical values, " +
                $"expected {physical.Length}.");
    }

    static void DecodeDefinitionBitset(ReadOnlySpan<byte> payload, int valueCount, EncodingKind encoding,
        Span<byte> destination, out int nonNullCount)
    {
        destination.Clear();
        if (encoding == EncodingKind.BitPacked)
        {
            var requiredBytes = LegacyBitPackedDecoder.GetByteCount(valueCount, bitWidth: 1);
            if (payload.Length < requiredBytes)
                throw new CorruptParquetException(
                    $"Legacy bit-packed payload ({payload.Length} bytes) is too short to decode {valueCount} values.");
            var count = 0;
            for (var i = 0; i < valueCount; i++)
            {
                var value = (payload[i >> 3] >> (7 - (i & 7))) & 1;
                destination[i >> 3] |= (byte)(value << (i & 7));
                count += value;
            }
            nonNullCount = count;
            return;
        }
        if (encoding != EncodingKind.Rle)
            throw new NotSupportedException($"Definition level encoding '{encoding}' is not supported.");

        var valueOffset = 0;
        var nonNulls = 0;
        while (valueOffset < valueCount)
        {
            var header = ReadUnsignedVarInt(ref payload);
            if ((header & 1U) == 0)
            {
                var runLength = header >> 1;
                if (runLength == 0)
                    throw new CorruptParquetException("Definition levels contain an empty RLE run.");
                var repeated = ReadLittleEndian(ref payload, byteWidth: 1);
                if ((uint)repeated > 1)
                    throw new CorruptParquetException(
                        $"Definition level {repeated} exceeds the schema maximum of 1.");
                var runCopyLength = (int)Math.Min(runLength, checked((uint)(valueCount - valueOffset)));
                if (repeated != 0)
                {
                    SetDefinitionBits(destination, valueOffset, runCopyLength);
                    nonNulls += runCopyLength;
                }
                valueOffset += runCopyLength;
                continue;
            }

            var literalGroupCount = header >> 1;
            if (literalGroupCount == 0 || literalGroupCount > uint.MaxValue / 8)
                throw new CorruptParquetException(
                    $"Definition levels literal run group count {literalGroupCount} is invalid.");
            var literalCount = literalGroupCount * 8U;
            var literalByteCount = checked((int)literalGroupCount);
            if (literalByteCount > payload.Length)
                throw new CorruptParquetException(
                    $"Definition level literal group claims {literalByteCount} bytes but only {payload.Length} remain.");
            var literalCopyLength = (int)Math.Min(literalCount, checked((uint)(valueCount - valueOffset)));
            CopyDefinitionBits(payload[..literalByteCount], destination, valueOffset, literalCopyLength,
                ref nonNulls);
            valueOffset += literalCopyLength;
            payload = payload[literalByteCount..];
        }
        nonNullCount = nonNulls;
    }

    static void SetDefinitionBits(Span<byte> destination, int valueOffset, int valueCount)
    {
        while (valueCount != 0 && (valueOffset & 7) != 0)
        {
            destination[valueOffset >> 3] |= (byte)(1 << (valueOffset & 7));
            valueOffset++;
            valueCount--;
        }
        var byteCount = valueCount >> 3;
        destination.Slice(valueOffset >> 3, byteCount).Fill(byte.MaxValue);
        valueOffset += byteCount << 3;
        valueCount -= byteCount << 3;
        if (valueCount != 0)
            destination[valueOffset >> 3] |= (byte)((1 << valueCount) - 1);
    }

    static void CopyDefinitionBits(ReadOnlySpan<byte> source, Span<byte> destination,
        int valueOffset, int valueCount, ref int nonNullCount)
    {
        if ((valueOffset & 7) == 0)
        {
            var byteCount = valueCount >> 3;
            source[..byteCount].CopyTo(destination[(valueOffset >> 3)..]);
            for (var i = 0; i < byteCount; i++)
                nonNullCount += DefinitionByteCounts[source[i]];
            valueOffset += byteCount << 3;
            source = source[byteCount..];
            valueCount -= byteCount << 3;
        }
        for (var i = 0; i < valueCount; i++)
        {
            var value = (source[i >> 3] >> (i & 7)) & 1;
            destination[valueOffset >> 3] |= (byte)(value << (valueOffset & 7));
            nonNullCount += value;
            valueOffset++;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    static void ExpandDefinitionBitset(ReadOnlySpan<byte> source, int valueOffset, Span<byte> destination,
        out int nonNullCount)
    {
        var count = 0;
        var destinationOffset = 0;
        if ((valueOffset & 7) == 0)
        {
            var sourceOffset = valueOffset >> 3;
            var byteCount = destination.Length >> 3;
            ref var output = ref MemoryMarshal.GetReference(destination);
            for (var i = 0; i < byteCount; i++)
            {
                var packed = source[sourceOffset + i];
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref output, i << 3), ExpandedDefinitionBytes[packed]);
                count += DefinitionByteCounts[packed];
            }
            destinationOffset = byteCount << 3;
            valueOffset += destinationOffset;
        }
        for (; destinationOffset < destination.Length; destinationOffset++, valueOffset++)
        {
            var value = (byte)((source[valueOffset >> 3] >> (valueOffset & 7)) & 1);
            destination[destinationOffset] = value;
            count += value;
        }
        nonNullCount = count;
    }

    static void ExpandDefinitionBitset(ReadOnlySpan<byte> source, int valueOffset, Span<int> destination,
        out int nonNullCount)
    {
        var count = 0;
        for (var i = 0; i < destination.Length; i++, valueOffset++)
        {
            var value = (source[valueOffset >> 3] >> (valueOffset & 7)) & 1;
            destination[i] = value;
            count += value;
        }
        nonNullCount = count;
    }

    static ulong[] CreateExpandedDefinitionBytes()
    {
        var expanded = new ulong[256];
        var bytes = MemoryMarshal.AsBytes(expanded.AsSpan());
        for (var value = 0; value < expanded.Length; value++)
            for (var bit = 0; bit < 8; bit++)
                bytes[value * sizeof(ulong) + bit] = (byte)((value >> bit) & 1);
        return expanded;
    }

    static byte[] CreateDefinitionByteCounts()
    {
        var counts = new byte[256];
        for (var value = 0; value < counts.Length; value++)
        {
            var remaining = value;
            while (remaining != 0)
            {
                counts[value]++;
                remaining &= remaining - 1;
            }
        }
        return counts;
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

        var physicalType = GetPhysicalDecodeType<T>(column);
        var converter = column.Converter;
        if (header.Type is not (PageHeaderType.DataPage or PageHeaderType.DataPageV2) ||
            (converter is null ? physicalType == typeof(T) : !converter.IsNullableValueType(typeof(T))) ||
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

        var valueCount = PageValueCount(header, "values");
        var definitionLevelEncoding = header.Type == PageHeaderType.DataPage
            ? header.DefinitionLevelEncoding
            : EncodingKind.Rle;
        if (converter is null && typeof(T) == typeof(DateTime?) && physicalType == typeof(DateTime) &&
            column.PhysicalType == ParquetPhysicalType.Int64 && header.Encoding == EncodingKind.Plain &&
            (definitionPayload.IsEmpty || definitionLevelEncoding == EncodingKind.Rle))
        {
            var timestampPhysicalCount = DecodeNullablePlainDateTimes(dataPayload, definitionPayload, valueCount,
                definitionLevelEncoding, column.LogicalType, ref state, bufferPool);
            if (header.Type == PageHeaderType.DataPageV2 && timestampPhysicalCount != expectedPhysicalCount)
                throw new CorruptParquetException(
                    $"Definition levels contain {timestampPhysicalCount} values, expected {expectedPhysicalCount}.");
            buffer = state.CreateNativeBuffer(valueCount);
            return true;
        }

        if (converter is null && TryDecodeNullableNumericValuesByPhysicalType(dataPayload,
                definitionPayload, valueCount, column, header.Encoding, definitionLevelEncoding,
                physicalType, ref state, bufferPool, out var numericPhysicalCount))
        {
            if (header.Type == PageHeaderType.DataPageV2 && numericPhysicalCount != expectedPhysicalCount)
                throw new CorruptParquetException(
                    $"Definition levels contain {numericPhysicalCount} values, expected {expectedPhysicalCount}.");
            buffer = state.CreateNativeBuffer(valueCount);
            return true;
        }

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
            definitionLevelEncoding,
            physicalType, ref state, bufferPool);
        if (!decoded)
            return false;

        buffer = state.CreateNativeBuffer(valueCount);
        return true;
    }

    static bool TryDecodeNullableNumericValuesByPhysicalType<T>(ReadOnlySpan<byte> payload,
        ReadOnlySpan<byte> definitionPayload, int valueCount, Column column, EncodingKind encoding,
        EncodingKind definitionLevelEncoding, Type physicalType, ref ColumnReadBuffers<T> state,
        IParquetBufferPool bufferPool, out int physicalCount)
    {
        if (typeof(T) == typeof(int?) && physicalType == typeof(int))
            return TryDecodeNullableNumericValues<T, int>(payload, definitionPayload, valueCount,
                column, encoding, definitionLevelEncoding, ref state, bufferPool, out physicalCount);
        if (typeof(T) == typeof(long?) && physicalType == typeof(long))
            return TryDecodeNullableNumericValues<T, long>(payload, definitionPayload, valueCount,
                column, encoding, definitionLevelEncoding, ref state, bufferPool, out physicalCount);
        if (typeof(T) == typeof(float?) && physicalType == typeof(float))
            return TryDecodeNullableNumericValues<T, float>(payload, definitionPayload, valueCount,
                column, encoding, definitionLevelEncoding, ref state, bufferPool, out physicalCount);
        if (typeof(T) == typeof(double?) && physicalType == typeof(double))
            return TryDecodeNullableNumericValues<T, double>(payload, definitionPayload, valueCount,
                column, encoding, definitionLevelEncoding, ref state, bufferPool, out physicalCount);

        physicalCount = 0;
        return false;
    }

    static bool TryDecodeNullableNumericValues<T, TValue>(ReadOnlySpan<byte> payload,
        ReadOnlySpan<byte> definitionPayload, int valueCount, Column column, EncodingKind encoding,
        EncodingKind definitionLevelEncoding, ref ColumnReadBuffers<T> state, IParquetBufferPool bufferPool,
        out int physicalCount)
        where TValue : struct
    {
        if (encoding is not (EncodingKind.Plain or EncodingKind.ByteStreamSplit or
                EncodingKind.RleDictionary or EncodingKind.PlainDictionary))
        {
            physicalCount = 0;
            return false;
        }

        var values = state.GetValues<T>(valueCount, bufferPool);
        var destination = Unsafe.As<Span<T>, Span<TValue?>>(ref values);
        var definitions = AsBytes(destination)[..valueCount];
        if (definitionPayload.IsEmpty)
        {
            definitions.Fill(1);
            physicalCount = valueCount;
        }
        else
            DecodeCompactDefinitionLevels(definitionPayload, definitionLevelEncoding,
                definitions, out physicalCount);

        if (physicalCount == 0)
        {
            destination.Clear();
            return true;
        }

        var physicalByteLength = checked(physicalCount * Unsafe.SizeOf<TValue>());
        var physicalValues = MemoryMarshal.Cast<byte, TValue>(
            state.GetScratch(physicalByteLength, bufferPool));
        if (encoding is EncodingKind.RleDictionary or EncodingKind.PlainDictionary)
        {
            if (!state.HasDictionary)
                return false;
            DecodeDictionaryIndexesIntoBuffer(payload, checked((uint)physicalCount),
                state.GetDictionary<TValue>(), physicalValues);
        }
        else if (!TryDecodeValuesIntoNative(payload, column, checked((uint)physicalCount), encoding,
                     physicalValues))
        {
            return false;
        }

        var physicalIndex = physicalCount;
        for (var i = definitions.Length - 1; i >= 0; i--)
            destination[i] = definitions[i] == 0 ? null : physicalValues[--physicalIndex];
        if (physicalIndex != 0)
            throw new CorruptParquetException(
                $"Definition levels consumed {physicalCount - physicalIndex} physical values, " +
                $"expected {physicalCount}.");
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
        if (physicalType == typeof(decimal))
            return TryDecodeNullableValues<T, decimal>(payload, definitionPayload, valueCount, physicalCount,
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
        if (physicalType == typeof(Guid))
            return TryDecodeNullableValues<T, Guid>(payload, definitionPayload, valueCount, physicalCount,
                column, encoding, definitionLevelEncoding, ref state, bufferPool);
        return false;
    }

    static int DecodeNullablePlainDateTimes<T>(ReadOnlySpan<byte> payload,
        ReadOnlySpan<byte> definitionPayload, int valueCount, EncodingKind definitionLevelEncoding,
        LogicalType? logicalType, ref ColumnReadBuffers<T> state, IParquetBufferPool bufferPool)
    {
        if (logicalType is not LogicalType.Timestamp timestamp)
            throw new CorruptParquetException("Timestamp projection requires a timestamp logical type.");
        var ticksPerUnit = timestamp.Unit switch
        {
            TimeUnit.Millis => TimeSpan.TicksPerMillisecond,
            TimeUnit.Micros => 10,
            TimeUnit.Nanos => 0,
            _ => throw new CorruptParquetException("Timestamp projection requires a timestamp logical type.")
        };
        var kind = timestamp.IsAdjustedToUtc ? DateTimeKind.Utc : DateTimeKind.Unspecified;

        var values = state.GetValues<T>(valueCount, bufferPool);
        var destination = Unsafe.As<Span<T>, Span<DateTime?>>(ref values);
        if (definitionPayload.IsEmpty)
        {
            for (var i = 0; i < destination.Length; i++)
                destination[i] = DecodePlainDateTime(payload, i, ticksPerUnit, kind);
            return valueCount;
        }
        if (definitionLevelEncoding != EncodingKind.Rle)
            throw new NotSupportedException(
                $"Definition level encoding '{definitionLevelEncoding}' is not supported by the timestamp fast path.");

        var valueIndex = 0;
        var physicalIndex = 0;
        while (valueIndex < destination.Length)
        {
            var header = ReadUnsignedVarInt(ref definitionPayload);
            if ((header & 1U) == 0)
            {
                var runLength = header >> 1;
                if (runLength == 0)
                    throw new CorruptParquetException("Definition levels contain an empty RLE run.");
                var repeated = ReadLittleEndian(ref definitionPayload, byteWidth: 1);
                var copyLength = (int)Math.Min(runLength,
                    checked((uint)(destination.Length - valueIndex)));
                if (repeated == 0)
                    destination.Slice(valueIndex, copyLength).Clear();
                else
                    for (var i = 0; i < copyLength; i++)
                        destination[valueIndex + i] = DecodePlainDateTime(
                            payload, physicalIndex++, ticksPerUnit, kind);
                valueIndex += copyLength;
                continue;
            }

            var literalGroupCount = header >> 1;
            if (literalGroupCount == 0)
                throw new CorruptParquetException("Definition levels contain an empty bit-packed run.");
            if (literalGroupCount > uint.MaxValue / 8)
                throw new CorruptParquetException(
                    $"Definition levels literal run group count {literalGroupCount} is too large.");
            var literalCount = literalGroupCount * 8U;
            for (var i = 0U; i < literalCount && valueIndex < destination.Length; i++)
            {
                if (ReadBitPackedValue(ref definitionPayload, bitWidth: 1, checked((int)i)) == 0)
                    destination[valueIndex] = null;
                else
                    destination[valueIndex] = DecodePlainDateTime(payload, physicalIndex++, ticksPerUnit, kind);
                valueIndex++;
            }

            var literalByteCount = (literalCount + 7U) >> 3;
            if (literalByteCount > (uint)definitionPayload.Length)
                throw new CorruptParquetException(
                    $"Definition level literal group claims {literalByteCount} bytes but only {definitionPayload.Length} remain.");
            definitionPayload = definitionPayload[checked((int)literalByteCount)..];
        }
        return physicalIndex;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static DateTime DecodePlainDateTime(ReadOnlySpan<byte> payload, int physicalIndex,
        long ticksPerUnit, DateTimeKind kind)
    {
        var offset = checked(physicalIndex * sizeof(long));
        if (payload.Length - offset < sizeof(long))
            throw new CorruptParquetException(
                $"Payload ({payload.Length} bytes) is too short to decode timestamp value {physicalIndex}.");
        var raw = BinaryPrimitives.ReadInt64LittleEndian(payload.Slice(offset, sizeof(long)));
        return new DateTime(TimestampTicksScaled(raw, ticksPerUnit), kind);
    }

    static bool TryDecodeNullableValues<T, TValue>(ReadOnlySpan<byte> payload,
        ReadOnlySpan<byte> definitionPayload, int valueCount, int physicalCount, Column column,
        EncodingKind encoding, EncodingKind definitionLevelEncoding, ref ColumnReadBuffers<T> state,
        IParquetBufferPool bufferPool)
        where TValue : struct
    {
        var converter = column.Converter;
        var converted = converter is not null && converter.PhysicalType == typeof(TValue) &&
            converter.IsNullableValueType(typeof(T));
        if (!converted && typeof(T) != typeof(TValue?))
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

        if (encoding is EncodingKind.RleDictionary or EncodingKind.PlainDictionary)
        {
            if (!state.HasDictionary)
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
        if (converted)
        {
            converter!.ConvertNullableFromPhysical(MemoryMarshal.AsBytes(physicalValues), definitions,
                AsBytes(destination), physicalCount);
            return true;
        }

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

        var converter = column.Converter;
        if (header.Type is not (PageHeaderType.DataPage or PageHeaderType.DataPageV2) ||
            column.Options.Repetition == ParquetRepetition.Repeated ||
            RuntimeHelpers.IsReferenceOrContainsReferences<T>() ||
            (converter is null
                ? GetPhysicalDecodeType<T>() != typeof(T)
                : !converter.SupportsValueType(typeof(T)) || converter.IsNullableValueType(typeof(T))))
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

        var valueCount = PageValueCount(header, "values");
        if (converter is not null)
            return TryDecodeConvertedRequiredByPhysicalType(dataPayload, valueCount, column, header.Encoding,
                converter.PhysicalType, converter, ref state, bufferPool, out buffer);

        var destination = state.GetValues<T>(valueCount, bufferPool);
        if (header.Encoding is EncodingKind.RleDictionary or EncodingKind.PlainDictionary)
        {
            if (!state.HasDictionary)
                return false;
            DecodeDictionaryIndexesIntoBuffer(dataPayload, header.ValueCount,
                state.GetDictionary<T>(), destination);
        }
        else if (typeof(T) == typeof(DateTime) &&
            column.Options.Repetition == ParquetRepetition.Required &&
            column.PhysicalType == ParquetPhysicalType.Int64 &&
            header.Encoding == EncodingKind.Plain)
        {
            ValidatePlainPayload(dataPayload, header.ValueCount, sizeof(long));
            DecodePlainDateTimes(dataPayload,
                Unsafe.As<Span<T>, Span<DateTime>>(ref destination), column.LogicalType);
        }
        else if (!TryDecodeValuesIntoNative(dataPayload, column, header.ValueCount, header.Encoding, destination))
        {
            return false;
        }

        buffer = state.CreateNativeBuffer(valueCount);
        return true;
    }

    static void DecodePlainDateTimes(ReadOnlySpan<byte> payload, Span<DateTime> destination,
        LogicalType? logicalType)
    {
        if (BitConverter.IsLittleEndian)
        {
            MaterializeDateTimes(MemoryMarshal.Cast<byte, long>(payload)[..destination.Length],
                destination, logicalType);
            return;
        }

        var timestamp = GetTimestampLogicalType(logicalType);
        var kind = timestamp.IsAdjustedToUtc ? DateTimeKind.Utc : DateTimeKind.Unspecified;
        for (var i = 0; i < destination.Length; i++)
        {
            var raw = BinaryPrimitives.ReadInt64LittleEndian(payload.Slice(i * sizeof(long), sizeof(long)));
            destination[i] = new DateTime(TimestampTicks(raw, timestamp.Unit), kind);
        }
    }

    static void MaterializeDateTimes(ReadOnlySpan<long> raw, Span<DateTime> destination,
        LogicalType? logicalType)
    {
        // This is the bulk twin of DecodeDateTime and has to reject the same
        // values: building the DateTime inline threw ArgumentOutOfRangeException
        // for a raw timestamp outside the representable range, and the checked
        // arithmetic threw OverflowException before that. Both bypass the
        // CorruptParquetException a reader is documented to throw, so the range
        // checking lives in TimestampTicks and both paths go through it.
        var timestamp = GetTimestampLogicalType(logicalType);
        var kind = timestamp.IsAdjustedToUtc ? DateTimeKind.Utc : DateTimeKind.Unspecified;
        for (var i = 0; i < destination.Length; i++)
            destination[i] = new DateTime(TimestampTicks(raw[i], timestamp.Unit), kind);
    }

    static LogicalType.Timestamp GetTimestampLogicalType(LogicalType? logicalType)
        => logicalType as LogicalType.Timestamp ??
            throw new CorruptParquetException("Timestamp projection requires a timestamp logical type.");

    static bool TryDecodeConvertedRequiredByPhysicalType<T>(ReadOnlySpan<byte> payload, int valueCount,
        Column column, EncodingKind encoding, Type physicalType, ParquetValueConverter converter,
        ref ColumnReadBuffers<T> state, IParquetBufferPool bufferPool, out ColumnBuffer<T> buffer)
    {
        if (physicalType == typeof(int))
            return TryDecodeConvertedRequired<T, int>(payload, valueCount, column, encoding, converter,
                ref state, bufferPool, out buffer);
        if (physicalType == typeof(long))
            return TryDecodeConvertedRequired<T, long>(payload, valueCount, column, encoding, converter,
                ref state, bufferPool, out buffer);
        if (physicalType == typeof(bool))
            return TryDecodeConvertedRequired<T, bool>(payload, valueCount, column, encoding, converter,
                ref state, bufferPool, out buffer);
        if (physicalType == typeof(float))
            return TryDecodeConvertedRequired<T, float>(payload, valueCount, column, encoding, converter,
                ref state, bufferPool, out buffer);
        if (physicalType == typeof(double))
            return TryDecodeConvertedRequired<T, double>(payload, valueCount, column, encoding, converter,
                ref state, bufferPool, out buffer);
        if (physicalType == typeof(byte))
            return TryDecodeConvertedRequired<T, byte>(payload, valueCount, column, encoding, converter,
                ref state, bufferPool, out buffer);
        if (physicalType == typeof(ushort))
            return TryDecodeConvertedRequired<T, ushort>(payload, valueCount, column, encoding, converter,
                ref state, bufferPool, out buffer);
        if (physicalType == typeof(uint))
            return TryDecodeConvertedRequired<T, uint>(payload, valueCount, column, encoding, converter,
                ref state, bufferPool, out buffer);
        if (physicalType == typeof(ulong))
            return TryDecodeConvertedRequired<T, ulong>(payload, valueCount, column, encoding, converter,
                ref state, bufferPool, out buffer);
        if (physicalType == typeof(DateOnly))
            return TryDecodeConvertedRequired<T, DateOnly>(payload, valueCount, column, encoding, converter,
                ref state, bufferPool, out buffer);
        if (physicalType == typeof(DateTime))
            return TryDecodeConvertedRequired<T, DateTime>(payload, valueCount, column, encoding, converter,
                ref state, bufferPool, out buffer);
        if (physicalType == typeof(DateTimeOffset))
            return TryDecodeConvertedRequired<T, DateTimeOffset>(payload, valueCount, column, encoding, converter,
                ref state, bufferPool, out buffer);
        if (physicalType == typeof(TimeOnly))
            return TryDecodeConvertedRequired<T, TimeOnly>(payload, valueCount, column, encoding, converter,
                ref state, bufferPool, out buffer);
        if (physicalType == typeof(Guid))
            return TryDecodeConvertedRequired<T, Guid>(payload, valueCount, column, encoding, converter,
                ref state, bufferPool, out buffer);

        buffer = default;
        return false;
    }

    static bool TryDecodeConvertedRequired<T, TPhysical>(ReadOnlySpan<byte> payload, int valueCount,
        Column column, EncodingKind encoding, ParquetValueConverter converter, ref ColumnReadBuffers<T> state,
        IParquetBufferPool bufferPool, out ColumnBuffer<T> buffer)
        where TPhysical : struct
    {
        var physicalValues = MemoryMarshal.Cast<byte, TPhysical>(
            state.GetScratch(checked(valueCount * Unsafe.SizeOf<TPhysical>()), bufferPool));
        if (encoding is EncodingKind.RleDictionary or EncodingKind.PlainDictionary)
        {
            if (!state.HasDictionary)
            {
                buffer = default;
                return false;
            }
            DecodeDictionaryIndexesIntoBuffer(payload, checked((uint)valueCount),
                state.GetDictionary<TPhysical>(), physicalValues);
        }
        else if (!TryDecodeValuesIntoNative(payload, column, checked((uint)valueCount), encoding, physicalValues))
        {
            buffer = default;
            return false;
        }

        var destination = state.GetValues<T>(valueCount, bufferPool);
        converter.ConvertFromPhysical(MemoryMarshal.AsBytes(physicalValues), AsBytes(destination), valueCount);
        buffer = state.CreateNativeBuffer(valueCount);
        return true;
    }

    // header.ValueCount is file-controlled and only bounded by its own width, so
    // casting it straight to int let a corrupt page ask the buffer pool for a
    // two-gigabyte level buffer, where payloadOffset + capacity overflowed and
    // surfaced as OverflowException rather than the CorruptParquetException a
    // reader is documented to throw. It cannot be bounded against the payload:
    // levels are run-length encoded, so a legitimate all-null page really can
    // describe sixteen thousand values in four bytes.
    static int PageValueCount(PageHeader header, string what)
    {
        if (header.ValueCount > MaximumPageValueCount)
            throw new CorruptParquetException(
                $"Page declares {header.ValueCount} {what}, which exceeds the maximum of {MaximumPageValueCount}.");
        return (int)header.ValueCount;
    }

    // Leaves room for the pool's own alignment header on top of the widest
    // per-value buffer this reader builds (two levels of four bytes each).
    const int MaximumPageValueCount = (int.MaxValue - 1024) / (2 * sizeof(int));

    static bool TryDecodeBinaryDictionaryPage<T>(PageHeader header, ReadOnlySpan<byte> payload,
        Column column, ref ColumnReadBuffers<T> state, IParquetBufferPool bufferPool)
    {
        if (!IsBinaryPhysicalType(column.PhysicalType) || header.Encoding != EncodingKind.Plain)
            return false;

        // Every plain-encoded dictionary entry costs at least its 4-byte length
        // prefix, or its fixed width, so a page cannot hold more entries than
        // that. Unlike the data-page path, which bounds ValueCount against the
        // row count, this one trusted the header outright: a corrupt count let
        // the casts below overflow and escape as OverflowException instead of
        // CorruptParquetException, before ReadPlainBinaryLengths ever got the
        // chance to notice the payload was too short.
        var minimumEntryBytes = column.PhysicalType == ParquetPhysicalType.ByteArray
            ? sizeof(int)
            : GetFixedBinaryLength(column);
        var maximumValueCount = payload.Length / minimumEntryBytes;
        if (header.ValueCount > (uint)maximumValueCount)
            throw new CorruptParquetException(
                $"Dictionary page declares {header.ValueCount} values, but its {payload.Length}-byte payload holds at most {maximumValueCount}.");

        var valueCount = (int)header.ValueCount;
        var scratchByteLength = (long)valueCount * sizeof(int);
        if (scratchByteLength > int.MaxValue)
            throw new CorruptParquetException(
                $"Dictionary page value count ({valueCount}) needs more than {int.MaxValue} bytes of length scratch.");

        var lengths = MemoryMarshal.Cast<byte, int>(
            state.GetScratch((int)scratchByteLength, bufferPool));
        var payloadByteLength = ReadPlainBinaryLengths(payload, column, valueCount, lengths);
        var destination = state.GetBinaryDictionary(valueCount, payloadByteLength, bufferPool,
            out var destinationPayload);
        destination.Clear();
        FillPlainBinaryValues(payload, column, valueCount, lengths, [], destination,
            destinationPayload);
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

        var valueCount = PageValueCount(header, "values");
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
        if (encoding is EncodingKind.RleDictionary or EncodingKind.PlainDictionary)
        {
            if (!state.HasDictionary)
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
        if (column.PhysicalType == ParquetPhysicalType.ByteArray)
        {
            var prefixByteLength = checked(physicalCount * sizeof(int));
            if (prefixByteLength > payload.Length)
                throw new CorruptParquetException(
                    $"Payload ({payload.Length} bytes) is too short to decode {physicalCount} byte array length prefixes.");
            var byteArrayPayloadLength = payload.Length - prefixByteLength;
            var byteArrayDestination = state.GetBinaryValues(valueCount, byteArrayPayloadLength, bufferPool,
                out var byteArrayPayload);
            byteArrayDestination.Clear();

            var remaining = payload;
            var logicalIndex = 0;
            var destinationOffset = 0;
            for (var physicalIndex = 0; physicalIndex < physicalCount; physicalIndex++)
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
                if (length > byteArrayPayload.Length - destinationOffset)
                    throw new CorruptParquetException(
                        $"Byte array length {length} exceeds remaining decoded payload " +
                        $"({byteArrayPayload.Length - destinationOffset} bytes).");

                var targetIndex = GetBinaryLogicalIndex(definitions, ref logicalIndex, physicalIndex);
                remaining[..length].CopyTo(byteArrayPayload[destinationOffset..]);
                byteArrayDestination[targetIndex] = new BinaryValueDescriptor(destinationOffset, length);
                remaining = remaining[length..];
                destinationOffset += length;
            }
            if (!remaining.IsEmpty)
                throw new CorruptParquetException(
                    $"Plain byte array payload contains {remaining.Length} trailing bytes.");
            if (destinationOffset != byteArrayPayload.Length)
                throw new CorruptParquetException(
                    $"Plain byte array lengths consume {destinationOffset} bytes, expected " +
                    $"{byteArrayPayload.Length} bytes.");
            return;
        }

        var payloadByteLength = ReadPlainBinaryLengths(payload, column, physicalCount, lengths);
        var destination = state.GetBinaryValues(valueCount, payloadByteLength, bufferPool,
            out var destinationPayload);
        destination.Clear();
        FillPlainBinaryValues(payload, column, physicalCount, lengths, definitions, destination,
            destinationPayload);
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
        var logicalIndex = 0;
        var destinationOffset = 0;
        for (var physicalIndex = 0; physicalIndex < physicalCount; physicalIndex++)
        {
            var targetIndex = GetBinaryLogicalIndex(definitions, ref logicalIndex, physicalIndex);
            var valueDestination = destinationPayload.Slice(destinationOffset, valueLength);
            for (var lane = 0; lane < valueLength; lane++)
                valueDestination[lane] = payload[(lane * physicalCount) + physicalIndex];
            destination[targetIndex] = new BinaryValueDescriptor(destinationOffset, valueLength);
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
        var logicalIndex = 0;
        var destinationOffset = 0;
        for (var physicalIndex = 0; physicalIndex < physicalCount; physicalIndex++)
        {
            var length = lengths[physicalIndex];
            var targetIndex = GetBinaryLogicalIndex(definitions, ref logicalIndex, physicalIndex);
            remaining[..length].CopyTo(destinationPayload[destinationOffset..]);
            destination[targetIndex] = new BinaryValueDescriptor(destinationOffset, length);
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
                destination[previousLogicalIndex].GetSpan(payloadAddress)[..prefixLength].CopyTo(valueDestination);
            suffixRemaining[..suffixLength].CopyTo(valueDestination[prefixLength..]);
            suffixRemaining = suffixRemaining[suffixLength..];
            destination[targetIndex] = new BinaryValueDescriptor(destinationOffset, length);
            previousLogicalIndex = targetIndex;
            destinationOffset += length;
        }
    }

    static void DecodeBinaryDictionaryValues<T>(ReadOnlySpan<byte> payload,
        ReadOnlySpan<int> definitions, int valueCount, int physicalCount, Span<int> indexes,
        ref ColumnReadBuffers<T> state, IParquetBufferPool bufferPool)
    {
        var dictionary = state.GetDictionary<BinaryValueDescriptor>();
        var dictionaryPayloadAddress = GetBinaryPayloadAddress(state.Dictionary, dictionary.Length);
        DecodeDictionaryIndexesIntoBuffer(payload, checked((uint)physicalCount), dictionary.Length, indexes);
        var payloadByteLength = 0;
        for (var i = 0; i < indexes.Length; i++)
            payloadByteLength = AddBinaryLength(payloadByteLength, dictionary[indexes[i]].Length);

        var destination = state.GetBinaryValues(valueCount, payloadByteLength, bufferPool,
            out var destinationPayload);
        destination.Clear();
        var logicalIndex = 0;
        var destinationOffset = 0;
        for (var physicalIndex = 0; physicalIndex < physicalCount; physicalIndex++)
        {
            var value = dictionary[indexes[physicalIndex]];
            var targetIndex = GetBinaryLogicalIndex(definitions, ref logicalIndex, physicalIndex);
            value.GetSpan(dictionaryPayloadAddress).CopyTo(destinationPayload[destinationOffset..]);
            destination[targetIndex] = new BinaryValueDescriptor(destinationOffset, value.Length);
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
        Span<byte> destinationPayload)
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
            destination[targetIndex] = new BinaryValueDescriptor(destinationOffset, length);
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
        if (typeof(T) == typeof(TimeOnly) && column.PhysicalType == ParquetPhysicalType.Int32)
        {
            ValidatePlainPayload(payload, valueCount, sizeof(int));
            var typed = Unsafe.As<Span<T>, Span<TimeOnly>>(ref destination);
            for (var i = 0; i < typed.Length; i++)
                typed[i] = DecodeTime(BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(i * 4, 4)),
                    column.LogicalType);
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
            DecodePlainDateTimes(payload, typed, column.LogicalType);
            return true;
        }
        if (typeof(T) == typeof(Guid) && column.PhysicalType == ParquetPhysicalType.FixedLenByteArray)
        {
            var valueLength = GetFixedBinaryLength(column);
            if (valueLength != 16)
                return false;
            GetFixedBinaryPayloadLength(payload, checked((int)valueCount), valueLength);
            var typed = Unsafe.As<Span<T>, Span<Guid>>(ref destination);
            for (var i = 0; i < typed.Length; i++)
                typed[i] = new Guid(payload.Slice(i * valueLength, valueLength), bigEndian: true);
            return true;
        }
        if (typeof(T) == typeof(decimal))
        {
            var typed = Unsafe.As<Span<T>, Span<decimal>>(ref destination);
            switch (column.PhysicalType)
            {
                case ParquetPhysicalType.Int32:
                    ValidatePlainPayload(payload, valueCount, sizeof(int));
                    for (var i = 0; i < typed.Length; i++)
                        typed[i] = ParquetDecimalConverter.FromInt32(
                            BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(i * sizeof(int), sizeof(int))),
                            column);
                    return true;
                case ParquetPhysicalType.Int64:
                    ValidatePlainPayload(payload, valueCount, sizeof(long));
                    for (var i = 0; i < typed.Length; i++)
                        typed[i] = ParquetDecimalConverter.FromInt64(
                            BinaryPrimitives.ReadInt64LittleEndian(payload.Slice(i * sizeof(long), sizeof(long))),
                            column);
                    return true;
                case ParquetPhysicalType.FixedLenByteArray:
                {
                    var valueLength = GetFixedBinaryLength(column);
                    ValidatePlainPayload(payload, valueCount, checked((uint)valueLength));
                    for (var i = 0; i < typed.Length; i++)
                        typed[i] = ParquetDecimalConverter.ReadBigEndian(
                            payload.Slice(i * valueLength, valueLength), column);
                    return true;
                }
                case ParquetPhysicalType.ByteArray:
                {
                    var offset = 0;
                    for (var i = 0; i < typed.Length; i++)
                    {
                        if (payload.Length - offset < sizeof(int))
                            throw new CorruptParquetException(
                                $"Plain decimal payload ended before length prefix {i}.");
                        var length = BinaryPrimitives.ReadInt32LittleEndian(payload[offset..]);
                        offset += sizeof(int);
                        if (length <= 0 || length > payload.Length - offset)
                            throw new CorruptParquetException(
                                $"Plain decimal value {i} declares invalid length {length}.");
                        typed[i] = ParquetDecimalConverter.ReadBigEndian(payload.Slice(offset, length), column);
                        offset += length;
                    }
                    return true;
                }
            }
        }

        return false;
    }

    // ByteStreamSplit stores each byte of a value in its own lane, so decoding
    // value i reads payload[i], payload[count + i], ... one per byte of width.
    // The int32/int64 decoders validate that the payload actually holds those
    // lanes; the branches for the narrower CLR projections (byte, ushort, uint,
    // decimal, temporal) indexed them directly and read past the end of a short
    // payload, which surfaced as IndexOutOfRangeException.
    static void RequireByteStreamSplitLanes(ReadOnlySpan<byte> payload, uint valueCount, int width,
        Column column)
    {
        var required = (long)valueCount * width;
        if (required > payload.Length)
            throw new CorruptParquetException(
                $"ByteStreamSplit payload ({payload.Length} bytes) is too short for {valueCount} " +
                $"{width}-byte values in column '{column.Name}'.");
    }

    static bool TryDecodeByteStreamSplitIntoNative<T>(ReadOnlySpan<byte> payload, Column column,
        uint valueCount, Span<T> destination)
    {
        RequireByteStreamSplitLanes(payload, valueCount,
            column.PhysicalType == ParquetPhysicalType.Int64 ? sizeof(long) : sizeof(int), column);
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
            case ParquetPhysicalType.Int32 when typeof(T) == typeof(TimeOnly):
            {
                var typed = Unsafe.As<Span<T>, Span<TimeOnly>>(ref destination);
                var raw = MemoryMarshal.Cast<TimeOnly, int>(typed)[..typed.Length];
                DecodeByteStreamSplitInt32(payload, raw);
                for (var i = typed.Length - 1; i >= 0; i--)
                    typed[i] = DecodeTime(raw[i], column.LogicalType);
                return true;
            }
            case ParquetPhysicalType.Int64 when typeof(T) == typeof(DateTime):
            {
                var raw = Unsafe.As<Span<T>, Span<long>>(ref destination);
                DecodeByteStreamSplitInt64(payload, raw);
                var typed = Unsafe.As<Span<T>, Span<DateTime>>(ref destination);
                MaterializeDateTimes(raw, typed, column.LogicalType);
                return true;
            }
            case ParquetPhysicalType.Int64 when typeof(T) == typeof(DateTimeOffset):
            {
                var typed = Unsafe.As<Span<T>, Span<DateTimeOffset>>(ref destination);
                var raw = MemoryMarshal.Cast<DateTimeOffset, long>(typed)[..typed.Length];
                DecodeByteStreamSplitInt64(payload, raw);
                for (var i = typed.Length - 1; i >= 0; i--)
                    typed[i] = DecodeTimestamp(raw[i], column.LogicalType);
                return true;
            }
            case ParquetPhysicalType.FixedLenByteArray when typeof(T) == typeof(Guid):
            {
                var valueLength = GetFixedBinaryLength(column);
                if (valueLength != 16)
                    return false;
                GetFixedBinaryPayloadLength(payload, checked((int)valueCount), valueLength);
                var typed = Unsafe.As<Span<T>, Span<Guid>>(ref destination);
                Span<byte> guidBytes = stackalloc byte[16];
                for (var i = 0; i < typed.Length; i++)
                {
                    for (var lane = 0; lane < guidBytes.Length; lane++)
                        guidBytes[lane] = payload[(lane * typed.Length) + i];
                    typed[i] = new Guid(guidBytes, bigEndian: true);
                }
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
            case ParquetPhysicalType.Int32 when typeof(T) == typeof(decimal):
            {
                ValidatePlainPayload(payload, valueCount, sizeof(int));
                var typed = Unsafe.As<Span<T>, Span<decimal>>(ref destination);
                var raw = MemoryMarshal.Cast<decimal, int>(typed)[..typed.Length];
                DecodeByteStreamSplitInt32(payload, raw);
                for (var i = typed.Length - 1; i >= 0; i--)
                    typed[i] = ParquetDecimalConverter.FromInt32(raw[i], column);
                return true;
            }
            case ParquetPhysicalType.Int64 when typeof(T) == typeof(decimal):
            {
                ValidatePlainPayload(payload, valueCount, sizeof(long));
                var typed = Unsafe.As<Span<T>, Span<decimal>>(ref destination);
                var raw = MemoryMarshal.Cast<decimal, long>(typed)[..typed.Length];
                DecodeByteStreamSplitInt64(payload, raw);
                for (var i = typed.Length - 1; i >= 0; i--)
                    typed[i] = ParquetDecimalConverter.FromInt64(raw[i], column);
                return true;
            }
            case ParquetPhysicalType.FixedLenByteArray when typeof(T) == typeof(decimal):
            {
                var valueLength = GetFixedBinaryLength(column);
                ValidatePlainPayload(payload, valueCount, checked((uint)valueLength));
                var typed = Unsafe.As<Span<T>, Span<decimal>>(ref destination);
                Span<byte> encoded = valueLength <= 256 ? stackalloc byte[valueLength] : new byte[valueLength];
                for (var i = 0; i < typed.Length; i++)
                {
                    for (var lane = 0; lane < valueLength; lane++)
                        encoded[lane] = payload[(lane * typed.Length) + i];
                    typed[i] = ParquetDecimalConverter.ReadBigEndian(encoded, column);
                }
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
        if (column.PhysicalType == ParquetPhysicalType.Int32 && typeof(T) == typeof(decimal))
        {
            var typed = Unsafe.As<Span<T>, Span<decimal>>(ref destination);
            var raw = MemoryMarshal.Cast<decimal, int>(typed)[..typed.Length];
            DeltaBinaryPackedDecoder.ReadInt32(payload, raw);
            for (var i = typed.Length - 1; i >= 0; i--)
                typed[i] = ParquetDecimalConverter.FromInt32(raw[i], column);
            return true;
        }
        if (column.PhysicalType == ParquetPhysicalType.Int64 && typeof(T) == typeof(decimal))
        {
            var typed = Unsafe.As<Span<T>, Span<decimal>>(ref destination);
            var raw = MemoryMarshal.Cast<decimal, long>(typed)[..typed.Length];
            DeltaBinaryPackedDecoder.ReadInt64(payload, raw);
            for (var i = typed.Length - 1; i >= 0; i--)
                typed[i] = ParquetDecimalConverter.FromInt64(raw[i], column);
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
        if (column.PhysicalType == ParquetPhysicalType.Int32 && typeof(T) == typeof(TimeOnly))
        {
            var typed = Unsafe.As<Span<T>, Span<TimeOnly>>(ref destination);
            var raw = MemoryMarshal.Cast<TimeOnly, int>(typed)[..typed.Length];
            DeltaBinaryPackedDecoder.ReadInt32(payload, raw);
            for (var i = typed.Length - 1; i >= 0; i--)
                typed[i] = DecodeTime(raw[i], column.LogicalType);
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
            if (column.LogicalType is not LogicalType.Timestamp timestamp)
                throw new CorruptParquetException(
                    "Timestamp projection requires a timestamp logical type.");

            var kind = timestamp.IsAdjustedToUtc ? DateTimeKind.Utc : DateTimeKind.Unspecified;
            for (var i = 0; i < typed.Length; i++)
                typed[i] = new DateTime(TimestampTicks(raw[i], timestamp.Unit), kind);
            return true;
        }
        if (column.PhysicalType == ParquetPhysicalType.Int64 && typeof(T) == typeof(DateTimeOffset))
        {
            var typed = Unsafe.As<Span<T>, Span<DateTimeOffset>>(ref destination);
            var raw = MemoryMarshal.Cast<DateTimeOffset, long>(typed)[..typed.Length];
            DeltaBinaryPackedDecoder.ReadInt64(payload, raw);
            for (var i = typed.Length - 1; i >= 0; i--)
                typed[i] = DecodeTimestamp(raw[i], column.LogicalType);
            return true;
        }
        return false;
    }

    // Every temporal value below is a raw number out of the file, and the .NET
    // types they build reject out-of-range input with ArgumentOutOfRangeException
    // — or overflow first, with OverflowException. Neither is what callers of the
    // reader are told to catch, so each conversion is range-checked here and a
    // value that cannot be represented is reported as the corrupt data it is.
    static DateOnly DecodeDate(int days)
    {
        var dayNumber = (long)UnixEpochDate.DayNumber + days;
        if (dayNumber < 0 || dayNumber > DateOnly.MaxValue.DayNumber)
            throw new CorruptParquetException(
                $"Date value {days} is outside the range representable by a date.");
        return DateOnly.FromDayNumber((int)dayNumber);
    }

    static TimeOnly DecodeTime(long raw, LogicalType? logicalType)
    {
        var ticks = logicalType switch
        {
            LogicalType.Time { Unit: TimeUnit.Millis } => ScaleTicks(raw, TimeSpan.TicksPerMillisecond, "Time"),
            LogicalType.Time { Unit: TimeUnit.Micros } => ScaleTicks(raw, 10, "Time"),
            LogicalType.Time { Unit: TimeUnit.Nanos } => raw / 100,
            _ => throw new CorruptParquetException("TimeOnly projection requires a time logical type.")
        };

        if (ticks < 0 || ticks > TimeOnly.MaxValue.Ticks)
            throw new CorruptParquetException(
                $"Time value {raw} is outside the range representable by a time of day.");
        return new TimeOnly(ticks);
    }

    // The scaling itself can overflow before the range check ever runs.
    static long ScaleTicks(long raw, long multiplier, string what)
    {
        var ticks = raw * multiplier;
        if (raw != 0 && (ticks / raw != multiplier || (raw == -1 && ticks == long.MinValue)))
            throw new CorruptParquetException($"{what} value {raw} overflows when scaled to ticks.");
        return ticks;
    }

    static DateTimeOffset DecodeTimestamp(long raw, LogicalType? logicalType)
    {
        if (logicalType is LogicalType.Timestamp { IsAdjustedToUtc: false })
            throw new NotSupportedException(
                "DateTimeOffset projection is not supported for timestamps with local semantics.");

        return DecodeTimestampValue(raw, logicalType);
    }

    static DateTimeOffset DecodeTimestampValue(long raw, LogicalType? logicalType)
    {
        var unit = logicalType is LogicalType.Timestamp timestamp
            ? timestamp.Unit
            : throw new CorruptParquetException("Timestamp projection requires a timestamp logical type.");
        return new DateTimeOffset(TimestampTicks(raw, unit), TimeSpan.Zero);
    }

    static DateTime DecodeDateTime(long raw, LogicalType? logicalType)
    {
        if (logicalType is not LogicalType.Timestamp timestamp)
            throw new CorruptParquetException("Timestamp projection requires a timestamp logical type.");

        return new DateTime(TimestampTicks(raw, timestamp.Unit),
            timestamp.IsAdjustedToUtc ? DateTimeKind.Utc : DateTimeKind.Unspecified);
    }

    // Some call sites precompute the multiplier, with 0 standing for nanos.
    static long TimestampTicksScaled(long raw, long ticksPerUnit)
        => TimestampTicks(raw, ticksPerUnit switch
        {
            0 => TimeUnit.Nanos,
            10 => TimeUnit.Micros,
            _ => TimeUnit.Millis
        });

    static long TimestampTicks(long raw, TimeUnit unit)
    {
        var offset = unit switch
        {
            TimeUnit.Millis => ScaleTicks(raw, TimeSpan.TicksPerMillisecond, "Timestamp"),
            TimeUnit.Micros => ScaleTicks(raw, 10, "Timestamp"),
            TimeUnit.Nanos => raw / 100,
            _ => throw new CorruptParquetException("Timestamp projection requires a timestamp logical type.")
        };

        var epoch = DateTime.UnixEpoch.Ticks;
        if (offset > long.MaxValue - epoch)
            throw new CorruptParquetException($"Timestamp value {raw} overflows when offset from the epoch.");

        var ticks = epoch + offset;
        if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
            throw new CorruptParquetException(
                $"Timestamp value {raw} is outside the range representable by a date and time.");
        return ticks;
    }

    internal static bool TryDecodePage<T>(PageHeader header, ReadOnlySpan<byte> payload, Column column,
        InternalColumnChunkMetadata columnChunk, ulong rowCount, ref Array? dictionary, ref T[]? dictionaryBuffer,
        ref T[]? valuesBuffer, IParquetBufferPool bufferPool, ref ParquetBuffer scratchBuffer,
        out ReadOnlyMemory<T> values, out EncodingKind encoding)
    {
        ArgumentNullException.ThrowIfNull(column);

        if (column.Options.Repetition == ParquetRepetition.Repeated)
            throw new NotSupportedException(
                $"Repeated column '{column.Name}' cannot be materialized as a flat managed buffer; use RowGroup.NestedColumn<T>.");

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
            throw new NotSupportedException(
                $"Repeated column '{column.Name}' cannot be materialized as a flat managed buffer; use RowGroup.NestedColumn<T>.");

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

        if (typeof(T) == typeof(decimal))
        {
            var typed = (decimal[])(object)EnsureManagedBuffer(ref valuesBuffer, valueCount);
            if (!TryDecodePlainIntoNative(payload, column, valueCount,
                    typed.AsSpan(0, checked((int)valueCount))))
            {
                values = default;
                return false;
            }
            values = new ReadOnlyMemory<T>(valuesBuffer!, 0, checked((int)valueCount));
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
        RequireByteStreamSplitLanes(payload, valueCount,
            column.PhysicalType == ParquetPhysicalType.Int64 ? sizeof(long) : sizeof(int), column);
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
            case ParquetPhysicalType.Int32 or ParquetPhysicalType.Int64 or ParquetPhysicalType.FixedLenByteArray
                when typeof(T) == typeof(decimal):
            {
                var typed = (decimal[])(object)EnsureManagedBuffer(ref valuesBuffer, valueCount);
                if (!TryDecodeByteStreamSplitIntoNative(payload, column, valueCount,
                        typed.AsSpan(0, checked((int)valueCount))))
                {
                    values = default;
                    return false;
                }
                values = new ReadOnlyMemory<T>(valuesBuffer!, 0, checked((int)valueCount));
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
            case ParquetPhysicalType.Int32 or ParquetPhysicalType.Int64 when typeof(T) == typeof(decimal):
            {
                var typed = (decimal[])(object)EnsureManagedBuffer(ref valuesBuffer, valueCount);
                if (!TryDecodeDeltaBinaryPackedIntoNative(payload, column,
                        typed.AsSpan(0, checked((int)valueCount))))
                {
                    values = default;
                    return false;
                }
                values = new ReadOnlyMemory<T>(valuesBuffer!, 0, checked((int)valueCount));
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
        => DecodeByteStreamSplitInt32Slice(payload, destination.Length, 0, destination);

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    static void DecodeByteStreamSplitInt32Slice(ReadOnlySpan<byte> payload, int totalCount, int valueOffset,
        Span<int> destination)
    {
        if (totalCount < 0 || valueOffset < 0 || valueOffset > totalCount - destination.Length)
            throw new ArgumentOutOfRangeException(nameof(valueOffset));
        if ((long)totalCount * 4 > payload.Length)
            throw new CorruptParquetException(
                $"ByteStreamSplit payload ({payload.Length} bytes) is too short for {totalCount} Int32 values.");

        ref var lane0 = ref MemoryMarshal.GetReference(payload);
        ref var lane1 = ref Unsafe.Add(ref lane0, totalCount);
        ref var lane2 = ref Unsafe.Add(ref lane1, totalCount);
        ref var lane3 = ref Unsafe.Add(ref lane2, totalCount);
        ref var values = ref Unsafe.As<int, uint>(ref MemoryMarshal.GetReference(destination));
        var length = (nuint)(uint)destination.Length;
        var sourceOffset = (nuint)(uint)valueOffset;
        nuint i = 0;

        if (Avx512F.IsSupported)
        {
            var vectorCount = (nuint)Vector128<byte>.Count;
            for (; length - i >= vectorCount; i += vectorCount)
            {
                var decoded = Avx512F.ConvertToVector512Int32(Vector128.LoadUnsafe(ref lane0, sourceOffset + i)).AsUInt32();
                decoded |= Vector512.ShiftLeft(
                    Avx512F.ConvertToVector512Int32(Vector128.LoadUnsafe(ref lane1, sourceOffset + i)).AsUInt32(), 8);
                decoded |= Vector512.ShiftLeft(
                    Avx512F.ConvertToVector512Int32(Vector128.LoadUnsafe(ref lane2, sourceOffset + i)).AsUInt32(), 16);
                decoded |= Vector512.ShiftLeft(
                    Avx512F.ConvertToVector512Int32(Vector128.LoadUnsafe(ref lane3, sourceOffset + i)).AsUInt32(), 24);
                decoded.StoreUnsafe(ref values, i);
            }
        }
        else if (Avx2.IsSupported)
        {
            var vectorCount = (nuint)Vector256<uint>.Count;
            for (; length - i >= vectorCount; i += vectorCount)
            {
                var decoded = Avx2.ConvertToVector256Int32(LoadLowerUInt64(ref lane0, sourceOffset + i)).AsUInt32();
                decoded |= Vector256.ShiftLeft(Avx2.ConvertToVector256Int32(LoadLowerUInt64(ref lane1, sourceOffset + i))
                    .AsUInt32(), 8);
                decoded |= Vector256.ShiftLeft(Avx2.ConvertToVector256Int32(LoadLowerUInt64(ref lane2, sourceOffset + i))
                    .AsUInt32(), 16);
                decoded |= Vector256.ShiftLeft(Avx2.ConvertToVector256Int32(LoadLowerUInt64(ref lane3, sourceOffset + i))
                    .AsUInt32(), 24);
                decoded.StoreUnsafe(ref values, i);
            }
        }

        for (; i < length; i++)
            Unsafe.Add(ref values, i) =
                Unsafe.Add(ref lane0, sourceOffset + i) |
                ((uint)Unsafe.Add(ref lane1, sourceOffset + i) << 8) |
                ((uint)Unsafe.Add(ref lane2, sourceOffset + i) << 16) |
                ((uint)Unsafe.Add(ref lane3, sourceOffset + i) << 24);
    }

    static void DecodeByteStreamSplitInt64(ReadOnlySpan<byte> payload, Span<long> destination)
    {
        var uintDestination = MemoryMarshal.Cast<long, ulong>(destination);
        DecodeByteStreamSplitUInt64(payload, uintDestination);
    }

    static void DecodeByteStreamSplitUInt64(ReadOnlySpan<byte> payload, Span<ulong> destination)
        => DecodeByteStreamSplitUInt64Slice(payload, destination.Length, 0, destination);

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    static void DecodeByteStreamSplitUInt64Slice(ReadOnlySpan<byte> payload, int totalCount, int valueOffset,
        Span<ulong> destination)
    {
        if (totalCount < 0 || valueOffset < 0 || valueOffset > totalCount - destination.Length)
            throw new ArgumentOutOfRangeException(nameof(valueOffset));
        if ((long)totalCount * 8 > payload.Length)
            throw new CorruptParquetException(
                $"ByteStreamSplit payload ({payload.Length} bytes) is too short for {totalCount} 8-byte values.");

        ref var lane0 = ref MemoryMarshal.GetReference(payload);
        ref var lane1 = ref Unsafe.Add(ref lane0, totalCount);
        ref var lane2 = ref Unsafe.Add(ref lane1, totalCount);
        ref var lane3 = ref Unsafe.Add(ref lane2, totalCount);
        ref var lane4 = ref Unsafe.Add(ref lane3, totalCount);
        ref var lane5 = ref Unsafe.Add(ref lane4, totalCount);
        ref var lane6 = ref Unsafe.Add(ref lane5, totalCount);
        ref var lane7 = ref Unsafe.Add(ref lane6, totalCount);
        ref var values = ref MemoryMarshal.GetReference(destination);
        var length = (nuint)(uint)destination.Length;
        var sourceOffset = (nuint)(uint)valueOffset;
        nuint i = 0;

        if (Avx512F.IsSupported)
        {
            var vectorCount = (nuint)Vector512<ulong>.Count;
            for (; length - i >= vectorCount; i += vectorCount)
            {
                var decoded = Avx512F.ConvertToVector512Int64(LoadLowerUInt64(ref lane0, sourceOffset + i)).AsUInt64();
                decoded |= Vector512.ShiftLeft(Avx512F.ConvertToVector512Int64(LoadLowerUInt64(ref lane1, sourceOffset + i))
                    .AsUInt64(), 8);
                decoded |= Vector512.ShiftLeft(Avx512F.ConvertToVector512Int64(LoadLowerUInt64(ref lane2, sourceOffset + i))
                    .AsUInt64(), 16);
                decoded |= Vector512.ShiftLeft(Avx512F.ConvertToVector512Int64(LoadLowerUInt64(ref lane3, sourceOffset + i))
                    .AsUInt64(), 24);
                decoded |= Vector512.ShiftLeft(Avx512F.ConvertToVector512Int64(LoadLowerUInt64(ref lane4, sourceOffset + i))
                    .AsUInt64(), 32);
                decoded |= Vector512.ShiftLeft(Avx512F.ConvertToVector512Int64(LoadLowerUInt64(ref lane5, sourceOffset + i))
                    .AsUInt64(), 40);
                decoded |= Vector512.ShiftLeft(Avx512F.ConvertToVector512Int64(LoadLowerUInt64(ref lane6, sourceOffset + i))
                    .AsUInt64(), 48);
                decoded |= Vector512.ShiftLeft(Avx512F.ConvertToVector512Int64(LoadLowerUInt64(ref lane7, sourceOffset + i))
                    .AsUInt64(), 56);
                decoded.StoreUnsafe(ref values, i);
            }
        }
        else if (Avx2.IsSupported)
        {
            var vectorCount = (nuint)Vector256<ulong>.Count;
            for (; length - i >= vectorCount; i += vectorCount)
            {
                var decoded = Avx2.ConvertToVector256Int64(LoadLowerUInt32(ref lane0, sourceOffset + i)).AsUInt64();
                decoded |= Vector256.ShiftLeft(Avx2.ConvertToVector256Int64(LoadLowerUInt32(ref lane1, sourceOffset + i))
                    .AsUInt64(), 8);
                decoded |= Vector256.ShiftLeft(Avx2.ConvertToVector256Int64(LoadLowerUInt32(ref lane2, sourceOffset + i))
                    .AsUInt64(), 16);
                decoded |= Vector256.ShiftLeft(Avx2.ConvertToVector256Int64(LoadLowerUInt32(ref lane3, sourceOffset + i))
                    .AsUInt64(), 24);
                decoded |= Vector256.ShiftLeft(Avx2.ConvertToVector256Int64(LoadLowerUInt32(ref lane4, sourceOffset + i))
                    .AsUInt64(), 32);
                decoded |= Vector256.ShiftLeft(Avx2.ConvertToVector256Int64(LoadLowerUInt32(ref lane5, sourceOffset + i))
                    .AsUInt64(), 40);
                decoded |= Vector256.ShiftLeft(Avx2.ConvertToVector256Int64(LoadLowerUInt32(ref lane6, sourceOffset + i))
                    .AsUInt64(), 48);
                decoded |= Vector256.ShiftLeft(Avx2.ConvertToVector256Int64(LoadLowerUInt32(ref lane7, sourceOffset + i))
                    .AsUInt64(), 56);
                decoded.StoreUnsafe(ref values, i);
            }
        }

        for (; i < length; i++)
            Unsafe.Add(ref values, i) =
                Unsafe.Add(ref lane0, sourceOffset + i) |
                ((ulong)Unsafe.Add(ref lane1, sourceOffset + i) << 8) |
                ((ulong)Unsafe.Add(ref lane2, sourceOffset + i) << 16) |
                ((ulong)Unsafe.Add(ref lane3, sourceOffset + i) << 24) |
                ((ulong)Unsafe.Add(ref lane4, sourceOffset + i) << 32) |
                ((ulong)Unsafe.Add(ref lane5, sourceOffset + i) << 40) |
                ((ulong)Unsafe.Add(ref lane6, sourceOffset + i) << 48) |
                ((ulong)Unsafe.Add(ref lane7, sourceOffset + i) << 56);
    }

    static bool TryDecodeByteStreamSplitSliceIntoNative<T>(ReadOnlySpan<byte> payload, Column column,
        int totalCount, int valueOffset, Span<T> destination)
        where T : struct
    {
        if (typeof(T) == typeof(int) || typeof(T) == typeof(long) ||
            typeof(T) == typeof(float) || typeof(T) == typeof(double))
        {
            DecodeByteStreamSplitNumericSlice(payload, totalCount, valueOffset, destination);
            return true;
        }

        switch (column.PhysicalType)
        {
            case ParquetPhysicalType.Int64 when typeof(T) == typeof(ulong):
                DecodeByteStreamSplitUInt64Slice(payload, totalCount, valueOffset,
                    Unsafe.As<Span<T>, Span<ulong>>(ref destination));
                return true;
            case ParquetPhysicalType.Int32 when typeof(T) == typeof(DateOnly):
            {
                var raw = Unsafe.As<Span<T>, Span<int>>(ref destination);
                DecodeByteStreamSplitInt32Slice(payload, totalCount, valueOffset, raw);
                var typed = Unsafe.As<Span<T>, Span<DateOnly>>(ref destination);
                for (var i = 0; i < typed.Length; i++)
                    typed[i] = DecodeDate(raw[i]);
                return true;
            }
            case ParquetPhysicalType.Int64 when typeof(T) == typeof(TimeOnly):
            {
                var raw = Unsafe.As<Span<T>, Span<long>>(ref destination);
                DecodeByteStreamSplitUInt64Slice(payload, totalCount, valueOffset,
                    MemoryMarshal.Cast<long, ulong>(raw));
                var typed = Unsafe.As<Span<T>, Span<TimeOnly>>(ref destination);
                for (var i = 0; i < typed.Length; i++)
                    typed[i] = DecodeTime(raw[i], column.LogicalType);
                return true;
            }
            case ParquetPhysicalType.Int32 when typeof(T) == typeof(TimeOnly):
            {
                var typed = Unsafe.As<Span<T>, Span<TimeOnly>>(ref destination);
                var raw = MemoryMarshal.Cast<TimeOnly, int>(typed)[..typed.Length];
                DecodeByteStreamSplitInt32Slice(payload, totalCount, valueOffset, raw);
                for (var i = typed.Length - 1; i >= 0; i--)
                    typed[i] = DecodeTime(raw[i], column.LogicalType);
                return true;
            }
            case ParquetPhysicalType.Int64 when typeof(T) == typeof(DateTime):
            {
                var raw = Unsafe.As<Span<T>, Span<long>>(ref destination);
                DecodeByteStreamSplitUInt64Slice(payload, totalCount, valueOffset,
                    MemoryMarshal.Cast<long, ulong>(raw));
                var typed = Unsafe.As<Span<T>, Span<DateTime>>(ref destination);
                MaterializeDateTimes(raw, typed, column.LogicalType);
                return true;
            }
            case ParquetPhysicalType.Int64 when typeof(T) == typeof(DateTimeOffset):
            {
                var typed = Unsafe.As<Span<T>, Span<DateTimeOffset>>(ref destination);
                var raw = MemoryMarshal.Cast<DateTimeOffset, long>(typed)[..typed.Length];
                DecodeByteStreamSplitUInt64Slice(payload, totalCount, valueOffset,
                    MemoryMarshal.Cast<long, ulong>(raw));
                for (var i = typed.Length - 1; i >= 0; i--)
                    typed[i] = DecodeTimestamp(raw[i], column.LogicalType);
                return true;
            }
            case ParquetPhysicalType.FixedLenByteArray when typeof(T) == typeof(Guid):
            {
                var valueLength = GetFixedBinaryLength(column);
                if (valueLength != 16)
                    return false;
                var typed = Unsafe.As<Span<T>, Span<Guid>>(ref destination);
                Span<byte> guidBytes = stackalloc byte[16];
                for (var i = 0; i < typed.Length; i++)
                {
                    for (var lane = 0; lane < guidBytes.Length; lane++)
                        guidBytes[lane] = payload[checked(lane * totalCount + valueOffset + i)];
                    typed[i] = new Guid(guidBytes, bigEndian: true);
                }
                return true;
            }
            case ParquetPhysicalType.Int32 when typeof(T) == typeof(byte):
            {
                var typed = Unsafe.As<Span<T>, Span<byte>>(ref destination);
                for (var i = 0; i < typed.Length; i++)
                {
                    var offset = valueOffset + i;
                    typed[i] = (byte)(payload[offset] | (payload[totalCount + offset] << 8) |
                        (payload[totalCount * 2 + offset] << 16) |
                        (payload[totalCount * 3 + offset] << 24));
                }
                return true;
            }
            case ParquetPhysicalType.Int32 when typeof(T) == typeof(ushort):
            {
                var typed = Unsafe.As<Span<T>, Span<ushort>>(ref destination);
                for (var i = 0; i < typed.Length; i++)
                {
                    var offset = valueOffset + i;
                    typed[i] = unchecked((ushort)(payload[offset] | (payload[totalCount + offset] << 8) |
                        (payload[totalCount * 2 + offset] << 16) |
                        (payload[totalCount * 3 + offset] << 24)));
                }
                return true;
            }
            case ParquetPhysicalType.Int32 when typeof(T) == typeof(uint):
            {
                var typed = Unsafe.As<Span<T>, Span<uint>>(ref destination);
                DecodeByteStreamSplitInt32Slice(payload, totalCount, valueOffset,
                    MemoryMarshal.Cast<uint, int>(typed));
                return true;
            }
            case ParquetPhysicalType.Int32 when typeof(T) == typeof(decimal):
            {
                var typed = Unsafe.As<Span<T>, Span<decimal>>(ref destination);
                var raw = MemoryMarshal.Cast<decimal, int>(typed)[..typed.Length];
                DecodeByteStreamSplitInt32Slice(payload, totalCount, valueOffset, raw);
                for (var i = typed.Length - 1; i >= 0; i--)
                    typed[i] = ParquetDecimalConverter.FromInt32(raw[i], column);
                return true;
            }
            case ParquetPhysicalType.Int64 when typeof(T) == typeof(decimal):
            {
                var typed = Unsafe.As<Span<T>, Span<decimal>>(ref destination);
                var raw = MemoryMarshal.Cast<decimal, long>(typed)[..typed.Length];
                DecodeByteStreamSplitUInt64Slice(payload, totalCount, valueOffset,
                    MemoryMarshal.Cast<long, ulong>(raw));
                for (var i = typed.Length - 1; i >= 0; i--)
                    typed[i] = ParquetDecimalConverter.FromInt64(raw[i], column);
                return true;
            }
            case ParquetPhysicalType.FixedLenByteArray when typeof(T) == typeof(decimal):
            {
                var valueLength = GetFixedBinaryLength(column);
                var typed = Unsafe.As<Span<T>, Span<decimal>>(ref destination);
                Span<byte> encoded = valueLength <= 256 ? stackalloc byte[valueLength] : new byte[valueLength];
                for (var i = 0; i < typed.Length; i++)
                {
                    for (var lane = 0; lane < valueLength; lane++)
                        encoded[lane] = payload[checked(lane * totalCount + valueOffset + i)];
                    typed[i] = ParquetDecimalConverter.ReadBigEndian(encoded, column);
                }
                return true;
            }
            default:
                return false;
        }
    }

    static void DecodeByteStreamSplitNumericSlice<T>(ReadOnlySpan<byte> payload, int totalCount,
        int valueOffset, Span<T> destination)
        where T : struct
    {
        if (typeof(T) == typeof(int))
            DecodeByteStreamSplitInt32Slice(payload, totalCount, valueOffset,
                Unsafe.As<Span<T>, Span<int>>(ref destination));
        else if (typeof(T) == typeof(long))
            DecodeByteStreamSplitUInt64Slice(payload, totalCount, valueOffset,
                MemoryMarshal.Cast<long, ulong>(Unsafe.As<Span<T>, Span<long>>(ref destination)));
        else if (typeof(T) == typeof(float))
            DecodeByteStreamSplitInt32Slice(payload, totalCount, valueOffset,
                MemoryMarshal.Cast<float, int>(Unsafe.As<Span<T>, Span<float>>(ref destination)));
        else if (typeof(T) == typeof(double))
            DecodeByteStreamSplitUInt64Slice(payload, totalCount, valueOffset,
                MemoryMarshal.Cast<double, ulong>(Unsafe.As<Span<T>, Span<double>>(ref destination)));
        else
            throw new InvalidOperationException($"Unsupported ByteStreamSplit numeric type '{typeof(T)}'.");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static Vector128<byte> LoadLowerUInt64(ref byte source, nuint offset)
        => Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, offset))).AsByte();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static Vector128<byte> LoadLowerUInt32(ref byte source, nuint offset)
        => Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref source, offset))).AsByte();

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

        if (bitWidth == 11 && destination.Length >= 8 && Avx2.IsSupported && Bmi2.X64.IsSupported)
        {
            var vectorizedLength = destination.Length & ~7;
            if (typeof(T) == typeof(int))
            {
                DecodeDictionaryLiteralInt32Indexes11Bit(payload,
                    Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<int>>(ref dictionary),
                    Unsafe.As<Span<T>, Span<int>>(ref destination)[..vectorizedLength]);
            }
            else if (typeof(T) == typeof(long) || typeof(T) == typeof(DateTime) || typeof(T) == typeof(double))
            {
                DecodeDictionaryLiteralInt64Indexes11Bit(payload,
                    Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<long>>(ref dictionary),
                    Unsafe.As<Span<T>, Span<long>>(ref destination)[..vectorizedLength]);
            }
            else
            {
                vectorizedLength = 0;
            }

            if (vectorizedLength != 0)
            {
                payload = payload[(vectorizedLength / 8 * 11)..];
                destination = destination[vectorizedLength..];
                if (destination.IsEmpty)
                    return;
            }
        }

        if (bitWidth is 19 or 20 && destination.Length >= 8 && Avx2.IsSupported &&
            (typeof(T) == typeof(long) || typeof(T) == typeof(DateTime)))
        {
            var vectorizedLength = destination.Length & ~7;
            DecodeDictionaryLiteralInt64IndexesWide(payload, bitWidth,
                Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<long>>(ref dictionary),
                Unsafe.As<Span<T>, Span<long>>(ref destination)[..vectorizedLength]);
            payload = payload[(vectorizedLength / 8 * bitWidth)..];
            destination = destination[vectorizedLength..];
            if (destination.IsEmpty)
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

    static unsafe void DecodeDictionaryLiteralInt32Indexes11Bit(ReadOnlySpan<byte> payload,
        ReadOnlySpan<int> dictionary, Span<int> destination)
    {
        const ulong laneMask = 0x07ff_07ff_07ff_07ffUL;
        ref var source = ref MemoryMarshal.GetReference(payload);
        ref var target = ref MemoryMarshal.GetReference(destination);
        var maximumIndex = Vector256.Create(dictionary.Length - 1);
        fixed (int* dictionaryPointer = dictionary)
        {
            var byteIndex = 0;
            for (var valueIndex = 0; valueIndex < destination.Length; valueIndex += 8, byteIndex += 11)
            {
                var lower = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteIndex));
                ulong upper = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref source, byteIndex + 5));
                upper |= (ulong)Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref source, byteIndex + 9)) << 32;
                var indexes = Avx2.ConvertToVector256Int32(Vector128.Create(
                    Bmi2.X64.ParallelBitDeposit(lower, laneMask),
                    Bmi2.X64.ParallelBitDeposit(upper >> 4, laneMask)).AsUInt16());
                if (Avx2.MoveMask(Avx2.CompareGreaterThan(indexes, maximumIndex).AsByte()) != 0)
                {
                    for (var lane = 0; lane < 8; lane++)
                        ValidateDictionaryIndex(indexes.GetElement(lane), dictionary.Length);
                }
                Avx2.GatherVector256(dictionaryPointer, indexes, sizeof(int))
                    .StoreUnsafe(ref target, (nuint)valueIndex);
            }
        }
    }

    static unsafe void DecodeDictionaryLiteralInt64Indexes11Bit(ReadOnlySpan<byte> payload,
        ReadOnlySpan<long> dictionary, Span<long> destination)
    {
        const ulong laneMask = 0x07ff_07ff_07ff_07ffUL;
        ref var source = ref MemoryMarshal.GetReference(payload);
        ref var target = ref MemoryMarshal.GetReference(destination);
        var maximumIndex = Vector256.Create(dictionary.Length - 1);
        fixed (long* dictionaryPointer = dictionary)
        {
            var byteIndex = 0;
            for (var valueIndex = 0; valueIndex < destination.Length; valueIndex += 8, byteIndex += 11)
            {
                var lower = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteIndex));
                ulong upper = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref source, byteIndex + 5));
                upper |= (ulong)Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref source, byteIndex + 9)) << 32;
                var indexes = Avx2.ConvertToVector256Int32(Vector128.Create(
                    Bmi2.X64.ParallelBitDeposit(lower, laneMask),
                    Bmi2.X64.ParallelBitDeposit(upper >> 4, laneMask)).AsUInt16());
                if (Avx2.MoveMask(Avx2.CompareGreaterThan(indexes, maximumIndex).AsByte()) != 0)
                {
                    for (var lane = 0; lane < 8; lane++)
                        ValidateDictionaryIndex(indexes.GetElement(lane), dictionary.Length);
                }
                Avx2.GatherVector256(dictionaryPointer, indexes.GetLower(), sizeof(long))
                    .StoreUnsafe(ref target, (nuint)valueIndex);
                Avx2.GatherVector256(dictionaryPointer, indexes.GetUpper(), sizeof(long))
                    .StoreUnsafe(ref target, (nuint)(valueIndex + 4));
            }
        }
    }

    static unsafe void DecodeDictionaryLiteralInt64IndexesWide(ReadOnlySpan<byte> payload, int bitWidth,
        ReadOnlySpan<long> dictionary, Span<long> destination)
    {
        ref var source = ref MemoryMarshal.GetReference(payload);
        ref var target = ref MemoryMarshal.GetReference(destination);
        var maximumIndex = Vector256.Create(dictionary.Length - 1);
        var mask = (1U << bitWidth) - 1U;
        fixed (long* dictionaryPointer = dictionary)
        {
            var byteIndex = 0;
            if (bitWidth == 20)
            {
                for (var valueIndex = 0; valueIndex < destination.Length; valueIndex += 8, byteIndex += 20)
                {
                    var a = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteIndex));
                    var b = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteIndex + 8));
                    var c = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref source, byteIndex + 16));
                    var indexes = Vector256.Create(
                        (int)(a & mask), (int)(a >> 20 & mask), (int)(a >> 40 & mask),
                        (int)((a >> 60 | b << 4) & mask),
                        (int)(b >> 16 & mask), (int)(b >> 36 & mask),
                        (int)((b >> 56 | (ulong)c << 8) & mask), (int)(c >> 12 & mask));
                    GatherDictionaryInt64(indexes, maximumIndex, dictionary.Length, dictionaryPointer,
                        ref target, valueIndex);
                }
            }
            else
            {
                for (var valueIndex = 0; valueIndex < destination.Length; valueIndex += 8, byteIndex += 19)
                {
                    var a = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteIndex));
                    var b = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteIndex + 8));
                    var c = (uint)(Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref source, byteIndex + 16)) |
                        Unsafe.Add(ref source, byteIndex + 18) << 16);
                    var indexes = Vector256.Create(
                        (int)(a & mask), (int)(a >> 19 & mask), (int)(a >> 38 & mask),
                        (int)((a >> 57 | b << 7) & mask),
                        (int)(b >> 12 & mask), (int)(b >> 31 & mask),
                        (int)((b >> 50 | (ulong)c << 14) & mask), (int)(c >> 5 & mask));
                    GatherDictionaryInt64(indexes, maximumIndex, dictionary.Length, dictionaryPointer,
                        ref target, valueIndex);
                }
            }
        }
    }

    static unsafe void GatherDictionaryInt64(Vector256<int> indexes, Vector256<int> maximumIndex,
        int dictionaryLength, long* dictionaryPointer, ref long target, int valueIndex)
    {
        if (Avx2.MoveMask(Avx2.CompareGreaterThan(indexes, maximumIndex).AsByte()) != 0)
        {
            for (var lane = 0; lane < 8; lane++)
                ValidateDictionaryIndex(indexes.GetElement(lane), dictionaryLength);
        }
        Avx2.GatherVector256(dictionaryPointer, indexes.GetLower(), sizeof(long))
            .StoreUnsafe(ref target, (nuint)valueIndex);
        Avx2.GatherVector256(dictionaryPointer, indexes.GetUpper(), sizeof(long))
            .StoreUnsafe(ref target, (nuint)(valueIndex + 4));
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
    {
        if (targetType == typeof(decimal))
        {
            var values = new decimal[checked((int)valueCount)];
            if (TryDecodePlainIntoNative(payload, column, valueCount, values))
                return values;
            throw new CorruptParquetException(
                $"Decimal column '{column.Name}' cannot be decoded from physical type '{column.PhysicalType}'.");
        }

        return column.PhysicalType switch
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
    }

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
        RequireByteStreamSplitLanes(payload, valueCount,
            column.PhysicalType == ParquetPhysicalType.Int64 ? sizeof(long) : sizeof(int), column);
        if (targetType == typeof(decimal))
        {
            var values = new decimal[checked((int)valueCount)];
            if (TryDecodeByteStreamSplitIntoNative(payload, column, valueCount, values))
                return values;
            throw new CorruptParquetException(
                $"Decimal column '{column.Name}' cannot be decoded with byte-stream split encoding.");
        }

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
        if (targetType == typeof(decimal))
        {
            if (column.PhysicalType == ParquetPhysicalType.Int32)
            {
                var raw = DeltaBinaryPackedDecoder.ReadInt32(payload);
                var values = new decimal[raw.Length];
                for (var i = 0; i < values.Length; i++)
                    values[i] = ParquetDecimalConverter.FromInt32(raw[i], column);
                return values;
            }
            if (column.PhysicalType == ParquetPhysicalType.Int64)
            {
                var raw = DeltaBinaryPackedDecoder.ReadInt64(payload);
                var values = new decimal[raw.Length];
                for (var i = 0; i < values.Length; i++)
                    values[i] = ParquetDecimalConverter.FromInt64(raw[i], column);
                return values;
            }
            throw new CorruptParquetException(
                $"Decimal column '{column.Name}' cannot be decoded with delta binary packed encoding.");
        }

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

    static Span<byte> AsBytes<T>(Span<T> values)
    {
        if (values.IsEmpty)
            return [];
        ref var first = ref Unsafe.As<T, byte>(ref MemoryMarshal.GetReference(values));
        return MemoryMarshal.CreateSpan(ref first, checked(values.Length * Unsafe.SizeOf<T>()));
    }

    static Type GetPhysicalDecodeType<T>(Column column)
    {
        var converter = column.Converter;
        if (converter is not null && converter.SupportsValueType(typeof(T)))
            return converter.PhysicalType;
        return GetPhysicalDecodeType<T>();
    }

    static Type GetPhysicalDecodeType<T>()
    {
        if (typeof(T) == typeof(int?)) return typeof(int);
        if (typeof(T) == typeof(long?)) return typeof(long);
        if (typeof(T) == typeof(bool?)) return typeof(bool);
        if (typeof(T) == typeof(float?)) return typeof(float);
        if (typeof(T) == typeof(double?)) return typeof(double);
        if (typeof(T) == typeof(decimal?)) return typeof(decimal);
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
        else if (typeof(T) == typeof(decimal?))
        {
            var src = (decimal[])physicalValues;
            var dst = (decimal?[])(object)result;
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

    static void DecodeCompactDefinitionLevels(ReadOnlySpan<byte> payload, EncodingKind encoding,
        Span<byte> destination, out int nonNullCount)
    {
        if (encoding == EncodingKind.BitPacked)
        {
            var byteCount = LegacyBitPackedDecoder.GetByteCount(destination.Length, bitWidth: 1);
            if (payload.Length < byteCount)
                throw new CorruptParquetException(
                    $"Legacy bit-packed payload ({payload.Length} bytes) is too short to decode " +
                    $"{destination.Length} values.");

            var count = 0;
            for (var i = 0; i < destination.Length; i++)
            {
                var value = (byte)((payload[i >> 3] >> (7 - (i & 7))) & 1);
                destination[i] = value;
                count += value;
            }
            nonNullCount = count;
            return;
        }
        if (encoding != EncodingKind.Rle)
            throw new NotSupportedException($"Definition level encoding '{encoding}' is not supported.");

        var valueIndex = 0;
        var nonNulls = 0;
        while (valueIndex < destination.Length)
        {
            var header = ReadUnsignedVarInt(ref payload);
            if ((header & 1U) == 0)
            {
                var runLength = header >> 1;
                if (runLength == 0)
                    throw new CorruptParquetException("Definition levels contain an empty RLE run.");
                var repeated = ReadLittleEndian(ref payload, byteWidth: 1);
                if ((uint)repeated > 1)
                    throw new CorruptParquetException(
                        $"Definition level {repeated} exceeds the schema maximum of 1.");
                var runCopyLength = (int)Math.Min(runLength,
                    checked((uint)(destination.Length - valueIndex)));
                destination.Slice(valueIndex, runCopyLength).Fill((byte)repeated);
                if (repeated != 0)
                    nonNulls += runCopyLength;
                valueIndex += runCopyLength;
                continue;
            }

            var literalGroupCount = header >> 1;
            if (literalGroupCount == 0 || literalGroupCount > uint.MaxValue / 8)
                throw new CorruptParquetException(
                    $"Definition levels literal run group count {literalGroupCount} is invalid.");
            var literalCount = literalGroupCount * 8U;
            var literalByteCount = (literalCount + 7U) >> 3;
            if (literalByteCount > (uint)payload.Length)
                throw new CorruptParquetException(
                    $"Definition level literal group claims {literalByteCount} bytes but only " +
                    $"{payload.Length} remain.");

            var copyLength = Math.Min(literalCount,
                checked((uint)(destination.Length - valueIndex)));
            for (var i = 0U; i < copyLength; i++)
            {
                var value = (byte)((payload[(int)(i >> 3)] >> ((int)i & 7)) & 1);
                destination[valueIndex++] = value;
                nonNulls += value;
            }
            payload = payload[checked((int)literalByteCount)..];
        }

        nonNullCount = nonNulls;
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
        else if (typeof(T) == typeof(decimal?))
        {
            var src = (decimal[])physicalValues;
            var dst = (decimal?[])(object)result;
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
