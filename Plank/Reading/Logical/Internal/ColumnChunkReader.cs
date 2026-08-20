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
    static readonly bool NullableDateTimeHasCanonicalLayout = HasCanonicalNullableDateTimeLayout();
    static readonly bool NullableInt32HasCanonicalLayout = HasCanonicalNullableInt32Layout();
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
        internal ReadOnlyMemory<byte> BorrowedPlainPayload;

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
        => TryStartFixedWidthPageBatches(header, payload, default, column, rowCount, ref buffers,
            bufferPool, ref page, out buffer);

    internal static bool TryStartFixedWidthPageBatches<T>(PageHeader header, ReadOnlySpan<byte> payload,
        ReadOnlyMemory<byte> borrowedPayload, Column column, ulong rowCount,
        ref ColumnReadBuffers<T> buffers, IParquetBufferPool bufferPool, ref FixedWidthPageState page,
        out ColumnBuffer<T> buffer)
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

        // An array-backed MemoryReadSource already keeps uncompressed page bytes alive. Required plain
        // primitive values have the same representation on little-endian hosts, so copying them into a
        // second pooled buffer only spends memory bandwidth. Retain() materializes an owned copy on demand.
        var borrowedPlainPayload = dataOffset == 0 && physicalCount == valueCount &&
            CanBorrowPlainValues<T>(column, physicalType, isRequired, decoderKind, borrowedPayload)
            ? borrowedPayload.Slice(dataOffset, checked(physicalCount * physicalSize))
            : default;

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
            IsNullable = isNullable,
            BorrowedPlainPayload = borrowedPlainPayload
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
        if (!page.BorrowedPlainPayload.IsEmpty)
        {
            var elementSize = Unsafe.SizeOf<T>();
            var byteOffset = checked(page.ValueOffset * elementSize);
            var byteLength = checked(batchCount * elementSize);
            var borrowed = new ColumnBuffer<T>(
                page.BorrowedPlainPayload.Slice(byteOffset, byteLength), batchCount, bufferPool);
            page.ValueOffset += batchCount;
            page.PhysicalOffset += batchCount;
            return borrowed;
        }
        var values = buffers.GetValues<T>(batchCount, bufferPool);
        int physicalBatchCount;
        ReadOnlySpan<byte> byteDefinitions = [];
        ReadOnlySpan<int> intDefinitions = [];
        if (!page.IsNullable)
            physicalBatchCount = batchCount;
        else if (column.Converter is null && page.PhysicalCount == page.ValueCount &&
                 page.DecoderKind is FixedWidthDecoderKind.Plain or FixedWidthDecoderKind.Dictionary &&
                 column.PhysicalType == ParquetPhysicalType.Int32 && typeof(T) == typeof(int?))
            // The page's definition stream was still decoded and validated when the batch state was
            // created. With every row present, keep it compact: the Int32 decoder below can expand
            // directly into nullable slots without materializing per-row definitions.
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

    static bool CanBorrowPlainValues<T>(Column column, Type physicalType, bool isRequired,
        FixedWidthDecoderKind decoderKind, ReadOnlyMemory<byte> borrowedPayload)
    {
        if (!BitConverter.IsLittleEndian || !isRequired || column.Converter is not null ||
            physicalType != typeof(T) || decoderKind != FixedWidthDecoderKind.Plain ||
            borrowedPayload.IsEmpty)
            return false;

        return column.PhysicalType switch
        {
            ParquetPhysicalType.Int32 => typeof(T) == typeof(int),
            ParquetPhysicalType.Int64 => typeof(T) == typeof(long),
            ParquetPhysicalType.Float => typeof(T) == typeof(float),
            ParquetPhysicalType.Double => typeof(T) == typeof(double),
            _ => false
        };
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

        if (converter is null && byteDefinitions.IsEmpty && physicalBatchCount == values.Length &&
            decoderKind is FixedWidthDecoderKind.Plain or FixedWidthDecoderKind.Dictionary &&
            column.PhysicalType == ParquetPhysicalType.Int32 &&
            typeof(T) == typeof(int?) && typeof(TValue) == typeof(int))
        {
            var nullableDestination = Unsafe.As<Span<T>, Span<int?>>(ref values);
            if (decoderKind == FixedWidthDecoderKind.Plain)
                DecodeAllPresentPlainInt32Batch(payload, physicalOffset, nullableDestination);
            else
                ExpandAllPresentInt32Batch(
                    MemoryMarshal.Cast<byte, int>(buffers.Scratch.Span)
                        .Slice(physicalOffset, physicalBatchCount), nullableDestination);
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

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    static void DecodeAllPresentPlainInt32Batch(ReadOnlySpan<byte> payload, int physicalOffset,
        Span<int?> destination)
        => DecodeAllPresentPlainInt32Batch(payload, physicalOffset, destination,
            BitConverter.IsLittleEndian, allowVector: true);

    internal static void DecodeAllPresentPlainInt32BatchForTesting(ReadOnlySpan<byte> payload,
        int physicalOffset, Span<int?> destination, bool nativeLittleEndian, bool allowVector)
        => DecodeAllPresentPlainInt32Batch(payload, physicalOffset, destination,
            nativeLittleEndian, allowVector);

    static void DecodeAllPresentPlainInt32Batch(ReadOnlySpan<byte> payload, int physicalOffset,
        Span<int?> destination, bool nativeLittleEndian, bool allowVector)
    {
        var byteOffset = checked(physicalOffset * sizeof(int));
        var byteLength = checked(destination.Length * sizeof(int));
        if ((uint)byteOffset > (uint)payload.Length || payload.Length - byteOffset < byteLength)
            throw new CorruptParquetException(
                $"Plain Int32 payload ({payload.Length} bytes) is too short for batch offset {physicalOffset} " +
                $"and {destination.Length} values.");

        var sourceBytes = payload.Slice(byteOffset, byteLength);
        if (!nativeLittleEndian)
        {
            for (var i = 0; i < destination.Length; i++)
                destination[i] = BinaryPrimitives.ReadInt32LittleEndian(sourceBytes[(i * sizeof(int))..]);
            return;
        }

        ExpandAllPresentInt32Batch(MemoryMarshal.Cast<byte, int>(sourceBytes), destination,
            allowVector);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    static void ExpandAllPresentInt32Batch(ReadOnlySpan<int> source, Span<int?> destination,
        bool allowVector = true)
    {
        System.Diagnostics.Debug.Assert(source.Length == destination.Length);
        var index = 0;
        if (allowVector && NullableInt32HasCanonicalLayout && Avx2.IsSupported &&
            destination.Length >= Vector256<ulong>.Count)
        {
            ref var sourceStart = ref MemoryMarshal.GetReference(source);
            ref var destinationStart = ref Unsafe.As<int?, ulong>(ref MemoryMarshal.GetReference(destination));
            var present = Vector256.Create(1UL);
            for (; index <= source.Length - Vector256<ulong>.Count; index += Vector256<ulong>.Count)
            {
                var values = Avx2.ConvertToVector256Int64(
                    Vector128.LoadUnsafe(ref sourceStart, (nuint)index)).AsUInt64();
                var nullable = Vector256.ShiftLeft(values, 32) | present;
                nullable.StoreUnsafe(ref destinationStart, (nuint)index);
            }
        }

        for (; index < source.Length; index++)
            destination[index] = source[index];
    }

    static bool HasCanonicalNullableInt32Layout()
    {
        if (Unsafe.SizeOf<int?>() != sizeof(ulong))
            return false;
        const int value = unchecked((int)0x8123_4567);
        int?[] probe = [value];
        ref var nullable = ref MemoryMarshal.GetArrayDataReference(probe);
        return Unsafe.As<int?, ulong>(ref nullable) == (1UL | (ulong)unchecked((uint)value) << 32);
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
                BooleanBitUnpacker.Unpack(payload, physicalOffset, booleans);
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

        if (converter is null && typeof(T) == typeof(DateTime?) && physicalType == typeof(DateTime) &&
            column.PhysicalType == ParquetPhysicalType.Int64 &&
            header.Encoding == EncodingKind.DeltaBinaryPacked)
        {
            var timestampPhysicalCount = DecodeNullableDeltaBinaryPackedDateTimes(dataPayload,
                definitionPayload, valueCount, definitionLevelEncoding, column.LogicalType,
                ref state, bufferPool);
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
                EncodingKind.RleDictionary or EncodingKind.PlainDictionary) &&
            !(encoding == EncodingKind.DeltaBinaryPacked && typeof(TValue) == typeof(int)))
        {
            physicalCount = 0;
            return false;
        }

        var values = state.GetValues<T>(valueCount, bufferPool);
        var destination = Unsafe.As<Span<T>, Span<TValue?>>(ref values);
        var definitions = AsBytes(destination)[..valueCount];
        var isDeltaInt32 = encoding == EncodingKind.DeltaBinaryPacked &&
            typeof(T) == typeof(int?) && typeof(TValue) == typeof(int);
        if (definitionPayload.IsEmpty)
        {
            physicalCount = valueCount;
            if (!isDeltaInt32)
                definitions.Fill(1);
        }
        else if (isDeltaInt32)
            physicalCount = CountCompactDefinitionLevels(definitionPayload,
                definitionLevelEncoding, valueCount);
        else
            DecodeCompactDefinitionLevels(definitionPayload, definitionLevelEncoding,
                definitions, out physicalCount);

        if (physicalCount == 0)
        {
            destination.Clear();
            return true;
        }

        if (isDeltaInt32 && physicalCount == valueCount)
        {
            var nullable = Unsafe.As<Span<TValue?>, Span<int?>>(ref destination);
            DeltaBinaryPackedDecoder.ReadNullableInt32(payload, nullable,
                NullableInt32HasCanonicalLayout);
            return true;
        }

        if (isDeltaInt32)
            DecodeCompactDefinitionLevels(definitionPayload, definitionLevelEncoding,
                definitions, out physicalCount);

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

    /// <summary>
    /// Decodes nullable delta-binary-packed timestamps without expanding every definition level to
    /// an <see cref="int"/> or materializing an intermediate <see cref="DateTime"/> array.
    /// </summary>
    /// <remarks>
    /// The generic converted-value path stores four bytes per definition, decodes physical values,
    /// materializes each timestamp in place, and then copies them into nullable destinations. Keep
    /// the definitions in the unused front of the destination instead. When every value is present,
    /// the first half of that destination can also hold the raw Int64 values: expanding the nullable
    /// values backwards only overwrites raw values which have already been consumed. Pages containing
    /// nulls keep the separate scratch buffer because their compact definitions share that storage.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    static int DecodeNullableDeltaBinaryPackedDateTimes<T>(ReadOnlySpan<byte> payload,
        ReadOnlySpan<byte> definitionPayload, int valueCount, EncodingKind definitionLevelEncoding,
        LogicalType? logicalType, ref ColumnReadBuffers<T> state, IParquetBufferPool bufferPool)
    {
        var timestamp = GetTimestampLogicalType(logicalType);
        var kind = timestamp.IsAdjustedToUtc ? DateTimeKind.Utc : DateTimeKind.Unspecified;
        var values = state.GetValues<T>(valueCount, bufferPool);
        var destination = Unsafe.As<Span<T>, Span<DateTime?>>(ref values);
        var definitions = AsBytes(destination)[..valueCount];
        int physicalCount;
        if (definitionPayload.IsEmpty)
        {
            definitions.Fill(1);
            physicalCount = valueCount;
        }
        else
        {
            DecodeCompactDefinitionLevels(definitionPayload, definitionLevelEncoding,
                definitions, out physicalCount);
        }

        if (physicalCount == valueCount)
        {
            var raw = MemoryMarshal.Cast<byte, long>(AsBytes(destination))[..physicalCount];
            DeltaBinaryPackedDecoder.ReadInt64(payload, raw);
            MaterializeAllPresentNullableDateTimes(raw, destination, timestamp.Unit, kind);
            return physicalCount;
        }

        var physicalByteLength = checked(physicalCount * sizeof(long));
        var physicalValues = MemoryMarshal.Cast<byte, long>(
            state.GetScratch(physicalByteLength, bufferPool));
        DeltaBinaryPackedDecoder.ReadInt64(payload, physicalValues);

        var physicalIndex = physicalCount;
        for (var i = definitions.Length - 1; i >= 0; i--)
            destination[i] = definitions[i] == 0
                ? null
                : new DateTime(TimestampTicks(physicalValues[--physicalIndex], timestamp.Unit), kind);
        if (physicalIndex != 0)
            throw new CorruptParquetException(
                $"Definition levels consumed {physicalCount - physicalIndex} physical values, " +
                $"expected {physicalCount}.");
        return physicalCount;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    static void MaterializeAllPresentNullableDateTimes(Span<long> raw, Span<DateTime?> destination,
        TimeUnit unit, DateTimeKind kind)
    {
        var index = destination.Length;
        if (unit == TimeUnit.Micros && NullableDateTimeHasCanonicalLayout && Avx2.IsSupported)
        {
            // These are the inclusive raw values whose scaled, epoch-adjusted ticks fit DateTime.
            // Validating before the shifts makes their unchecked vector arithmetic equivalent to
            // TimestampTicks, while an invalid vector falls back to that method for its exception.
            const long minimumMicroseconds = -62_135_596_800_000_000;
            const long maximumMicroseconds = 253_402_300_799_999_999;
            ref var rawStart = ref MemoryMarshal.GetReference(raw);
            ref var destinationStart = ref Unsafe.As<DateTime?, ulong>(
                ref MemoryMarshal.GetReference(destination));
            var minimum = Vector256.Create(minimumMicroseconds);
            var maximum = Vector256.Create(maximumMicroseconds);
            var epoch = Vector256.Create(DateTime.UnixEpoch.Ticks);
            var present = Vector256.Create(1UL);
            var kindProbe = new DateTime(0, kind);
            var kindBits = Vector256.Create(Unsafe.As<DateTime, ulong>(ref kindProbe));

            while ((index & (Vector256<long>.Count - 1)) != 0)
            {
                index--;
                destination[index] = new DateTime(TimestampTicks(raw[index], unit), kind);
            }

            while (index != 0)
            {
                var next = index - Vector256<long>.Count;
                var source = Vector256.LoadUnsafe(ref rawStart, (nuint)next);
                var invalid = Avx2.CompareGreaterThan(minimum, source) |
                    Avx2.CompareGreaterThan(source, maximum);
                if (Avx2.MoveMask(invalid.AsByte()) != 0)
                    break;

                var scaled = Avx2.ShiftLeftLogical(source.AsUInt64(), 3).AsInt64() +
                    Avx2.ShiftLeftLogical(source.AsUInt64(), 1).AsInt64();
                var dateData = (scaled + epoch).AsUInt64() | kindBits;
                var even = Avx2.UnpackLow(present, dateData);
                var odd = Avx2.UnpackHigh(present, dateData);
                // The canonical nullable layout is one present word followed by DateTime's data.
                Avx2.Permute2x128(even.AsInt64(), odd.AsInt64(), 0x20).AsUInt64()
                    .StoreUnsafe(ref destinationStart, (nuint)(next * 2));
                Avx2.Permute2x128(even.AsInt64(), odd.AsInt64(), 0x31).AsUInt64()
                    .StoreUnsafe(ref destinationStart, (nuint)(next * 2 + Vector256<ulong>.Count));
                index = next;
            }
        }

        while (index != 0)
        {
            index--;
            destination[index] = new DateTime(TimestampTicks(raw[index], unit), kind);
        }
    }

    static bool HasCanonicalNullableDateTimeLayout()
    {
        if (Unsafe.SizeOf<DateTime?>() != 2 * sizeof(ulong))
            return false;
        var value = new DateTime(0x1234_5678, DateTimeKind.Utc);
        DateTime?[] probe = [value];
        ref var nullable = ref MemoryMarshal.GetArrayDataReference(probe);
        ref var firstWord = ref Unsafe.As<DateTime?, ulong>(ref nullable);
        return firstWord == 1 && Unsafe.Add(ref firstWord, 1) == Unsafe.As<DateTime, ulong>(ref value);
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
            // DELTA_BYTE_ARRAY is defined for FIXED_LEN_BYTE_ARRAY as well as
            // BYTE_ARRAY, and parquet-mr's v2 writer uses it for both. The
            // reconstruction is the same either way — a prefix taken from the
            // previous value plus a suffix — so the only difference is that a
            // fixed-length column has a length to check the result against.
            case EncodingKind.DeltaByteArray when column.PhysicalType is ParquetPhysicalType.ByteArray
                    or ParquetPhysicalType.FixedLenByteArray:
                DecodeDeltaBinaryValues(payload, definitions, valueCount, physicalCount, column,
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
        int valueCount, int physicalCount, Column column, Span<int> scratch,
        ref ColumnReadBuffers<T> state, IParquetBufferPool bufferPool)
    {
        var fixedLength = column.PhysicalType == ParquetPhysicalType.FixedLenByteArray
            ? GetFixedBinaryLength(column)
            : -1;
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
            if (fixedLength >= 0 && previousLength != fixedLength)
                throw new CorruptParquetException(
                    $"Delta byte array value {i} reconstructs to {previousLength} bytes but column "
                    + $"'{column.Name}' is fixed at {fixedLength}.");
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
            case EncodingKind.Alp:
                return AlpDecoder.TryDecode(payload, column, valueCount, destination);
            case EncodingKind.DeltaBinaryPacked:
                return TryDecodeDeltaBinaryPackedIntoNative(payload, column, destination);
            case EncodingKind.DeltaByteArray:
                return TryDecodeDeltaByteArrayIntoNative(payload, column, valueCount, destination);
            default:
                return false;
        }
    }

    /// <summary>
    /// Decodes a DELTA_BYTE_ARRAY page of a fixed-length column into the CLR type
    /// its annotation calls for.
    /// </summary>
    /// <remarks>
    /// A FIXED_LEN_BYTE_ARRAY column annotated DECIMAL or UUID does not come back
    /// as a byte span — it goes through a converter into decimal or Guid — so it
    /// never reaches the binary decoders where the other DELTA_BYTE_ARRAY support
    /// lives. Without this the encoding was readable as raw bytes and not
    /// readable as the value it stands for.
    ///
    /// Each value is exactly the column's fixed length, so the prefix carried
    /// over from the previous value plus the suffix read here fill one buffer,
    /// and that buffer is what carries the prefix into the next value.
    /// </remarks>
    static bool TryDecodeDeltaByteArrayIntoNative<T>(ReadOnlySpan<byte> payload, Column column,
        uint valueCount, Span<T> destination)
    {
        if (column.PhysicalType != ParquetPhysicalType.FixedLenByteArray ||
            (typeof(T) != typeof(decimal) && typeof(T) != typeof(Guid)))
            return false;

        var valueLength = GetFixedBinaryLength(column);
        var count = checked((int)valueCount);
        var (prefixLengths, prefixConsumed) = valueCount == 0
            ? ([], 0)
            : DeltaBinaryPackedDecoder.ReadUInt32WithConsumedBytes(payload);
        var suffixPayload = payload[prefixConsumed..];
        var (suffixLengths, suffixConsumed) = valueCount == 0
            ? ([], 0)
            : DeltaBinaryPackedDecoder.ReadUInt32WithConsumedBytes(suffixPayload);
        if (prefixLengths.Length < count || suffixLengths.Length < count)
            throw new CorruptParquetException(
                $"Delta byte array page declares {valueCount} values but carries "
                + $"{Math.Min(prefixLengths.Length, suffixLengths.Length)} lengths.");

        var suffixes = suffixPayload[suffixConsumed..];
        Span<byte> value = valueLength <= 256 ? stackalloc byte[valueLength] : new byte[valueLength];
        value.Clear();

        for (var i = 0; i < count; i++)
        {
            var prefixLength = prefixLengths[i];
            var suffixLength = suffixLengths[i];
            if (prefixLength > (uint)valueLength || suffixLength > (uint)valueLength ||
                prefixLength + suffixLength != (uint)valueLength)
                throw new CorruptParquetException(
                    $"Delta byte array value {i} reconstructs to {prefixLength + suffixLength} bytes "
                    + $"but column '{column.Name}' is fixed at {valueLength}.");
            if (suffixLength > (uint)suffixes.Length)
                throw new CorruptParquetException(
                    $"Delta byte array suffix length {suffixLength} exceeds remaining suffix bytes "
                    + $"({suffixes.Length}).");

            // Bytes below prefixLength are already the previous value's, which is
            // exactly what the prefix means.
            suffixes[..(int)suffixLength].CopyTo(value[(int)prefixLength..]);
            suffixes = suffixes[(int)suffixLength..];

            if (typeof(T) == typeof(decimal))
                Unsafe.As<Span<T>, Span<decimal>>(ref destination)[i] =
                    ParquetDecimalConverter.ReadBigEndian(value, column);
            else
            {
                if (valueLength != 16)
                    return false;
                Unsafe.As<Span<T>, Span<Guid>>(ref destination)[i] = new Guid(value, bigEndian: true);
            }
        }

        return true;
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
            BooleanBitUnpacker.Unpack(payload, 0, typed);
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
    /// <summary>
    /// Scales a raw temporal value to ticks, rejecting the multiplications that would wrap.
    /// </summary>
    /// <remarks>
    /// The check was <c>ticks / raw != multiplier</c>, which costs a 64-bit division on every value
    /// decoded. Its divisor is the value being decoded, so it cannot be turned into a reciprocal
    /// multiply, and integer division is one of the widest performance spreads between machines:
    /// roughly 20 cycles on Zen 4 but up to 90 on the Intel parts this library also runs on. Reading a
    /// timestamp column cost 3.9x more than reading the same values as int64 there, against 1.5x on
    /// Zen 4 — the whole difference was this one instruction.
    ///
    /// Comparing against the largest magnitude that survives the multiply is exact, and every caller
    /// passes a literal multiplier, so the bounds fold into constants.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static long ScaleTicks(long raw, long multiplier, string what)
    {
        if (raw > long.MaxValue / multiplier || raw < long.MinValue / multiplier)
            throw new CorruptParquetException($"{what} value {raw} overflows when scaled to ticks.");
        return raw * multiplier;
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

    static void ValidatePlainPayload(ReadOnlySpan<byte> payload, uint valueCount, uint elementSize)
    {
        if (valueCount > (uint)payload.Length / elementSize)
            throw new CorruptParquetException(
                $"Payload ({payload.Length} bytes) is too short to decode {valueCount} plain values of {elementSize} bytes each.");
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

        if (bitWidth is 1 or 2 or 8 or 9 && typeof(T) == typeof(int) && destination.Length >= 8 &&
            Avx2.IsSupported && Bmi2.X64.IsSupported)
        {
            var vectorizedLength = destination.Length & ~7;
            DecodeDictionaryLiteralInt32IndexesSmall(payload, bitWidth,
                Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<int>>(ref dictionary),
                Unsafe.As<Span<T>, Span<int>>(ref destination)[..vectorizedLength]);
            payload = payload[(vectorizedLength / 8 * bitWidth)..];
            destination = destination[vectorizedLength..];
            if (destination.IsEmpty)
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

    static unsafe void DecodeDictionaryLiteralInt32IndexesSmall(ReadOnlySpan<byte> payload,
        int bitWidth, ReadOnlySpan<int> dictionary, Span<int> destination)
    {
        ref var source = ref MemoryMarshal.GetReference(payload);
        ref var target = ref MemoryMarshal.GetReference(destination);
        var maximumIndex = Vector256.Create(dictionary.Length - 1);
        var laneValue = (1UL << bitWidth) - 1UL;
        var laneMask = laneValue | laneValue << 16 | laneValue << 32 | laneValue << 48;
        fixed (int* dictionaryPointer = dictionary)
        {
            var byteIndex = 0;
            for (var valueIndex = 0; valueIndex < destination.Length;
                 valueIndex += 8, byteIndex += bitWidth)
            {
                ulong lower;
                ulong upper;
                if (bitWidth == 9)
                {
                    lower = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteIndex));
                    upper = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref source, byteIndex + 4));
                    upper |= (ulong)Unsafe.Add(ref source, byteIndex + 8) << 32;
                    upper >>= 4;
                }
                else
                {
                    var packed = bitWidth switch
                    {
                        1 => Unsafe.Add(ref source, byteIndex),
                        2 => Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref source, byteIndex)),
                        _ => Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteIndex))
                    };
                    lower = packed;
                    upper = packed >> (bitWidth * 4);
                }

                var indexes = Avx2.ConvertToVector256Int32(Vector128.Create(
                    Bmi2.X64.ParallelBitDeposit(lower, laneMask),
                    Bmi2.X64.ParallelBitDeposit(upper, laneMask)).AsUInt16());
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

    static void DecodeDictionaryLiteralInt32Indexes11Bit(ReadOnlySpan<byte> payload,
        ReadOnlySpan<int> dictionary, Span<int> destination)
    {
        var blockValueCount = dictionary.Length & ~7;
        if (blockValueCount >= 8 && destination.Length >= blockValueCount * 2)
        {
            var blockByteCount = checked(blockValueCount / 8 * 11);
            if (payload.Length >= blockByteCount * 2 &&
                payload[..blockByteCount].SequenceEqual(payload.Slice(blockByteCount, blockByteCount)))
            {
                var firstValues = destination[..blockValueCount];
                DecodeDictionaryLiteralInt32Indexes11BitCore(payload[..blockByteCount],
                    dictionary, firstValues);
                var valueOffset = blockValueCount;
                var byteOffset = blockByteCount;
                while (destination.Length - valueOffset >= blockValueCount &&
                       payload.Length - byteOffset >= blockByteCount &&
                       payload.Slice(byteOffset, blockByteCount)
                           .SequenceEqual(payload[..blockByteCount]))
                {
                    firstValues.CopyTo(destination[valueOffset..]);
                    valueOffset += blockValueCount;
                    byteOffset += blockByteCount;
                }

                if (valueOffset < destination.Length)
                    DecodeDictionaryLiteralInt32Indexes11BitCore(payload[byteOffset..],
                        dictionary, destination[valueOffset..]);
                return;
            }
        }

        DecodeDictionaryLiteralInt32Indexes11BitCore(payload, dictionary, destination);
    }

    static unsafe void DecodeDictionaryLiteralInt32Indexes11BitCore(ReadOnlySpan<byte> payload,
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

    static void DecodeDictionaryLiteralInt64Indexes11Bit(ReadOnlySpan<byte> payload,
        ReadOnlySpan<long> dictionary, Span<long> destination)
    {
        var blockValueCount = dictionary.Length & ~7;
        if (blockValueCount >= 8 && destination.Length >= blockValueCount * 2)
        {
            var blockByteCount = checked(blockValueCount / 8 * 11);
            if (payload.Length >= blockByteCount * 2 &&
                payload[..blockByteCount].SequenceEqual(payload.Slice(blockByteCount, blockByteCount)))
            {
                var firstValues = destination[..blockValueCount];
                DecodeDictionaryLiteralInt64Indexes11BitCore(payload[..blockByteCount],
                    dictionary, firstValues);
                var valueOffset = blockValueCount;
                var byteOffset = blockByteCount;
                while (destination.Length - valueOffset >= blockValueCount &&
                       payload.Length - byteOffset >= blockByteCount &&
                       payload.Slice(byteOffset, blockByteCount)
                           .SequenceEqual(payload[..blockByteCount]))
                {
                    firstValues.CopyTo(destination[valueOffset..]);
                    valueOffset += blockValueCount;
                    byteOffset += blockByteCount;
                }

                if (valueOffset < destination.Length)
                    DecodeDictionaryLiteralInt64Indexes11BitCore(payload[byteOffset..],
                        dictionary, destination[valueOffset..]);
                return;
            }
        }

        DecodeDictionaryLiteralInt64Indexes11BitCore(payload, dictionary, destination);
    }

    static void DecodeDictionaryLiteralInt64Indexes11BitCore(ReadOnlySpan<byte> payload,
        ReadOnlySpan<long> dictionary, Span<long> destination)
    {
        const ulong laneMask = 0x07ff_07ff_07ff_07ffUL;
        ref var source = ref MemoryMarshal.GetReference(payload);
        ref var dictionaryStart = ref MemoryMarshal.GetReference(dictionary);
        ref var target = ref MemoryMarshal.GetReference(destination);
        var maximumIndex = Vector256.Create(dictionary.Length - 1);
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
            Unsafe.Add(ref target, valueIndex) = Unsafe.Add(ref dictionaryStart, indexes.GetElement(0));
            Unsafe.Add(ref target, valueIndex + 1) = Unsafe.Add(ref dictionaryStart, indexes.GetElement(1));
            Unsafe.Add(ref target, valueIndex + 2) = Unsafe.Add(ref dictionaryStart, indexes.GetElement(2));
            Unsafe.Add(ref target, valueIndex + 3) = Unsafe.Add(ref dictionaryStart, indexes.GetElement(3));
            Unsafe.Add(ref target, valueIndex + 4) = Unsafe.Add(ref dictionaryStart, indexes.GetElement(4));
            Unsafe.Add(ref target, valueIndex + 5) = Unsafe.Add(ref dictionaryStart, indexes.GetElement(5));
            Unsafe.Add(ref target, valueIndex + 6) = Unsafe.Add(ref dictionaryStart, indexes.GetElement(6));
            Unsafe.Add(ref target, valueIndex + 7) = Unsafe.Add(ref dictionaryStart, indexes.GetElement(7));
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

    static int CountCompactDefinitionLevels(ReadOnlySpan<byte> payload, EncodingKind encoding,
        int valueCount)
    {
        if (encoding == EncodingKind.BitPacked)
            return LegacyBitPackedDecoder.CountSetBits(payload, valueCount);
        if (encoding != EncodingKind.Rle)
            throw new NotSupportedException($"Definition level encoding '{encoding}' is not supported.");

        var valueIndex = 0;
        var nonNulls = 0;
        while (valueIndex < valueCount)
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
                var copyLength = (int)Math.Min(runLength, checked((uint)(valueCount - valueIndex)));
                if (repeated != 0)
                    nonNulls += copyLength;
                valueIndex += copyLength;
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

            var count = Math.Min(literalCount, checked((uint)(valueCount - valueIndex)));
            var fullBytes = (int)(count >> 3);
            for (var i = 0; i < fullBytes; i++)
                nonNulls += DefinitionByteCounts[payload[i]];
            var trailingBits = (int)(count & 7);
            if (trailingBits != 0)
                nonNulls += DefinitionByteCounts[payload[fullBytes] & ((1 << trailingBits) - 1)];
            valueIndex += checked((int)count);
            payload = payload[checked((int)literalByteCount)..];
        }

        return nonNulls;
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
