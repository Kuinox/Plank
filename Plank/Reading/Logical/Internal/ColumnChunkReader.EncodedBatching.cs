using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Plank.Schema;

namespace Plank.Reading.Logical.Internal;

static partial class ColumnChunkReader
{
    static readonly Vector256<byte> DictionaryNineBitShuffle = Vector256.Create(
        Vector128.Create((byte)0, 1, 2, 3, 1, 2, 3, 4, 2, 3, 4, 5, 3, 4, 5, 6),
        Vector128.Create((byte)4, 5, 6, 7, 5, 6, 7, 8, 6, 7, 8, 9, 7, 8, 9, 10));
    static readonly Vector256<uint> DictionaryNineBitShifts =
        Vector256.Create(0U, 1, 2, 3, 4, 5, 6, 7);

    // The decoded byte target is deliberately soft. Stateful encodings advance by their cheapest
    // coarse unit (an RLE literal group, delta block, or ALP vector) and may overshoot; page state
    // keeps only cursors and the one previous value DELTA_BYTE_ARRAY intrinsically requires.
    internal enum EncodedDecoderKind : byte
    {
        None,
        BooleanRle,
        Dictionary,
        DeltaBinaryPacked,
        DeltaLengthByteArray,
        DeltaByteArray,
        Alp,
        PlainBinary,
        ByteStreamSplitBinary
    }

    internal struct RleBatchState
    {
        internal int Offset;
        internal int ValueCount;
        internal int ValuesRead;
        internal int BitWidth;
        internal uint RunRemaining;
        internal int RepeatedValue;
        internal bool LiteralRun;
    }

    internal struct EncodedPageState
    {
        internal int ValueCount;
        internal int ValueOffset;
        internal int PhysicalCount;
        internal int PhysicalOffset;
        internal int DataOffset;
        internal int DataLength;
        internal int DefinitionBitsetLength;
        internal int BatchElementSize;
        internal int BinaryDataOffset;
        internal int BinaryAuxOffset;
        internal int BinaryPayloadOffset;
        internal int BinaryFixedLength;
        internal int PreviousBinaryLength;
        internal EncodedDecoderKind DecoderKind;
        internal RleBatchState Rle;
        internal DeltaBinaryPackedDecoder.BatchState Delta;
        internal DeltaBinaryPackedDecoder.BatchState Delta2;
        internal AlpDecoder.BatchState Alp;
        internal bool IsNullable;
        internal bool IsBinary;
        internal ReadOnlyMemory<byte> BorrowedDataPayload;

        internal readonly bool Active
            => ValueOffset < ValueCount;
    }

    ref struct BinaryDefinitionCursor
    {
        readonly ReadOnlySpan<byte> _definitions;
        readonly int _valueOffset;
        readonly int _logicalCount;
        int _logicalIndex;

        internal BinaryDefinitionCursor(ReadOnlySpan<byte> definitions, int valueOffset,
            int logicalCount)
        {
            _definitions = definitions;
            _valueOffset = valueOffset;
            _logicalCount = logicalCount;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal int GetLogicalIndex(int physicalIndex)
        {
            if (_definitions.IsEmpty)
                return physicalIndex;
            while (_logicalIndex < _logicalCount)
            {
                var index = _logicalIndex++;
                var bitOffset = _valueOffset + index;
                if (((_definitions[bitOffset >> 3] >> (bitOffset & 7)) & 1) != 0)
                    return index;
            }
            throw new CorruptParquetException(
                "Definition levels contain fewer non-null values than the encoded payload.");
        }
    }

    internal static bool TryStartEncodedPageBatches<T>(PageHeader header,
        ReadOnlySpan<byte> payload, ReadOnlyMemory<byte> borrowedPayload, Column column,
        ulong rowCount, ref ColumnReadBuffers<T> buffers, IParquetBufferPool bufferPool,
        ref EncodedPageState page, out ColumnBuffer<T> buffer)
    {
        buffer = default;
        page = default;
        if (header.Type is not (PageHeaderType.DataPage or PageHeaderType.DataPageV2) ||
            header.ValueCount == 0 || column.Options.Repetition == ParquetRepetition.Repeated ||
            RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            return false;
        if (header.ValueCount > rowCount)
            throw new CorruptParquetException(
                $"Page value count ({header.ValueCount}) exceeds row group row count ({rowCount}).");

        var isBinary = typeof(T) == typeof(BinaryValueDescriptor) &&
            IsBinaryPhysicalType(column.PhysicalType);
        var physicalType = GetPhysicalDecodeType<T>(column);
        var converter = column.Converter;
        var isNullable = isBinary
            ? column.Options.Repetition == ParquetRepetition.Optional
            : converter is null
                ? physicalType != typeof(T)
                : converter.IsNullableValueType(typeof(T));
        var isRequired = isBinary || (converter is null
            ? physicalType == typeof(T)
            : converter.SupportsValueType(typeof(T)) && !isNullable);
        if ((!isNullable && !isRequired) ||
            (column.Options.Repetition == ParquetRepetition.Optional && !isNullable))
            return false;

        var decoderKind = GetEncodedDecoderKind(header.Encoding, column, physicalType, isBinary);
        if (decoderKind == EncodedDecoderKind.None)
            return false;

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
        else if (header.Type == PageHeaderType.DataPageV2 && header.NullCount == 0)
        {
            ValidateAllPresentDefinitionLevels(
                payload.Slice(definitionOffset, definitionLength), valueCount, definitionEncoding);
            physicalCount = valueCount;
        }
        else
        {
            definitionBitsetLength = checked((valueCount + 7) / 8);
            DecodeDefinitionBitset(payload.Slice(definitionOffset, definitionLength), valueCount,
                definitionEncoding, buffers.GetCompactDefinitions(definitionBitsetLength, bufferPool),
                out physicalCount);
            if (physicalCount == valueCount)
                definitionBitsetLength = 0;
        }
        if (header.Type == PageHeaderType.DataPageV2 && physicalCount != expectedPhysicalCount)
            throw new CorruptParquetException(
                $"Definition levels contain {physicalCount} values, expected {expectedPhysicalCount}.");

        var dataPayload = payload[dataOffset..];
        var state = new EncodedPageState
        {
            ValueCount = valueCount,
            PhysicalCount = physicalCount,
            DataOffset = dataOffset,
            DataLength = dataPayload.Length,
            DefinitionBitsetLength = definitionBitsetLength,
            BatchElementSize = isBinary
                ? EstimateBinaryBatchElementSize(dataPayload.Length, physicalCount,
                    decoderKind, ref buffers)
                : Math.Max(Unsafe.SizeOf<T>(), GetDecodedFixedWidthSize(physicalType)),
            BinaryFixedLength = column.PhysicalType is ParquetPhysicalType.FixedLenByteArray
                    or ParquetPhysicalType.Int96
                ? GetFixedBinaryLength(column)
                : -1,
            DecoderKind = decoderKind,
            IsNullable = isNullable,
            IsBinary = isBinary,
            BorrowedDataPayload = borrowedPayload.IsEmpty ? default : borrowedPayload[dataOffset..]
        };

        switch (decoderKind)
        {
            case EncodedDecoderKind.BooleanRle:
                _ = ReadBooleanRlePayload(dataPayload);
                state.Rle = StartRleBatch(dataPayload, physicalCount, bitWidth: 1,
                    initialOffset: sizeof(int));
                break;
            case EncodedDecoderKind.Dictionary:
                if (!buffers.HasDictionary)
                    return false;
                state.Rle = StartDictionaryRleBatch(dataPayload, physicalCount);
                break;
            case EncodedDecoderKind.DeltaBinaryPacked:
                state.Delta = DeltaBinaryPackedDecoder.StartBatch(dataPayload, physicalCount);
                break;
            case EncodedDecoderKind.Alp:
                state.Alp = AlpDecoder.StartBatch(dataPayload, physicalCount);
                break;
            case EncodedDecoderKind.DeltaLengthByteArray:
            {
                var lengthBytes = DeltaBinaryPackedDecoder.GetEncodedLength(dataPayload, physicalCount);
                state.Delta = DeltaBinaryPackedDecoder.StartBatch(dataPayload, physicalCount);
                state.BinaryDataOffset = lengthBytes;
                break;
            }
            case EncodedDecoderKind.DeltaByteArray:
            {
                var prefixBytes = DeltaBinaryPackedDecoder.GetEncodedLength(dataPayload, physicalCount);
                var suffixPayload = dataPayload[prefixBytes..];
                var suffixBytes = DeltaBinaryPackedDecoder.GetEncodedLength(suffixPayload, physicalCount);
                state.Delta = DeltaBinaryPackedDecoder.StartBatch(dataPayload, physicalCount);
                state.Delta2 = DeltaBinaryPackedDecoder.StartBatch(suffixPayload, physicalCount);
                state.BinaryAuxOffset = prefixBytes;
                state.BinaryDataOffset = checked(prefixBytes + suffixBytes);
                break;
            }
            case EncodedDecoderKind.ByteStreamSplitBinary:
                _ = GetFixedBinaryPayloadLength(dataPayload, physicalCount, state.BinaryFixedLength);
                break;
        }

        page = state;
        buffer = DecodeNextEncodedBatch(payload, column, ref buffers, bufferPool, ref page);
        return true;
    }

    static void ValidateAllPresentDefinitionLevels(ReadOnlySpan<byte> payload, int valueCount,
        EncodingKind encoding)
    {
        if (encoding != EncodingKind.Rle)
            throw new NotSupportedException(
                $"Definition level encoding '{encoding}' is not supported for data page v2.");

        var valueOffset = 0;
        while (valueOffset < valueCount)
        {
            var header = ReadUnsignedVarInt(ref payload);
            if ((header & 1U) == 0)
            {
                var runLength = header >> 1;
                if (runLength == 0)
                    throw new CorruptParquetException("Definition levels contain an empty RLE run.");
                var repeated = ReadLittleEndian(ref payload, byteWidth: 1);
                if (repeated != 1)
                    throw new CorruptParquetException(
                        "Definition levels contain nulls but the page header declares none.");
                valueOffset += (int)Math.Min(runLength,
                    checked((uint)(valueCount - valueOffset)));
                continue;
            }

            var literalGroupCount = header >> 1;
            if (literalGroupCount == 0 || literalGroupCount > uint.MaxValue / 8)
                throw new CorruptParquetException(
                    $"Definition levels literal run group count {literalGroupCount} is invalid.");
            var literalByteCount = checked((int)literalGroupCount);
            if (literalByteCount > payload.Length)
                throw new CorruptParquetException(
                    $"Definition level literal group claims {literalByteCount} bytes but only {payload.Length} remain.");
            var literalValueCount = (int)Math.Min(literalGroupCount * 8,
                checked((uint)(valueCount - valueOffset)));
            var fullBytes = literalValueCount >> 3;
            for (var i = 0; i < fullBytes; i++)
                if (payload[i] != byte.MaxValue)
                    throw new CorruptParquetException(
                        "Definition levels contain nulls but the page header declares none.");
            var remainingBits = literalValueCount & 7;
            if (remainingBits != 0 &&
                (payload[fullBytes] & ((1 << remainingBits) - 1)) != (1 << remainingBits) - 1)
                throw new CorruptParquetException(
                    "Definition levels contain nulls but the page header declares none.");
            valueOffset += literalValueCount;
            payload = payload[literalByteCount..];
        }
    }

    static EncodedDecoderKind GetEncodedDecoderKind(EncodingKind encoding, Column column,
        Type physicalType, bool isBinary)
    {
        if (isBinary)
            return encoding switch
            {
                EncodingKind.Plain => EncodedDecoderKind.PlainBinary,
                EncodingKind.ByteStreamSplit when column.PhysicalType == ParquetPhysicalType.FixedLenByteArray
                    => EncodedDecoderKind.ByteStreamSplitBinary,
                EncodingKind.RleDictionary or EncodingKind.PlainDictionary
                    => EncodedDecoderKind.Dictionary,
                EncodingKind.DeltaLengthByteArray when column.PhysicalType == ParquetPhysicalType.ByteArray
                    => EncodedDecoderKind.DeltaLengthByteArray,
                EncodingKind.DeltaByteArray when column.PhysicalType is ParquetPhysicalType.ByteArray
                        or ParquetPhysicalType.FixedLenByteArray
                    => EncodedDecoderKind.DeltaByteArray,
                _ => EncodedDecoderKind.None
            };

        if (encoding is EncodingKind.RleDictionary or EncodingKind.PlainDictionary)
            return CanBatchFixedWidthProjection(column, physicalType, encoding)
                ? EncodedDecoderKind.Dictionary
                : EncodedDecoderKind.None;
        if (encoding == EncodingKind.Rle && column.PhysicalType == ParquetPhysicalType.Boolean &&
            physicalType == typeof(bool))
            return EncodedDecoderKind.BooleanRle;
        if (encoding == EncodingKind.DeltaBinaryPacked &&
            column.PhysicalType is ParquetPhysicalType.Int32 or ParquetPhysicalType.Int64 &&
            CanBatchFixedWidthProjection(column, physicalType, encoding))
            return EncodedDecoderKind.DeltaBinaryPacked;
        if (encoding == EncodingKind.Alp &&
            ((column.PhysicalType == ParquetPhysicalType.Float && physicalType == typeof(float)) ||
             (column.PhysicalType == ParquetPhysicalType.Double && physicalType == typeof(double))))
            return EncodedDecoderKind.Alp;
        if (encoding == EncodingKind.DeltaByteArray &&
            column.PhysicalType == ParquetPhysicalType.FixedLenByteArray &&
            physicalType is not null && (physicalType == typeof(Guid) || physicalType == typeof(decimal)))
            return EncodedDecoderKind.DeltaByteArray;
        return EncodedDecoderKind.None;
    }

    static int EstimateBinaryBatchElementSize<T>(int dataLength, int physicalCount,
        EncodedDecoderKind decoderKind, ref ColumnReadBuffers<T> buffers)
    {
        var payloadBytes = physicalCount == 0 ? 0 : Math.Max(1, dataLength / physicalCount);
        if (decoderKind == EncodedDecoderKind.Dictionary && buffers.DictionaryCount != 0)
        {
            var dictionaryPayloadBytes = buffers.BorrowedBinaryDictionaryPayload.IsEmpty
                ? Math.Max(0, buffers.Dictionary.Length -
                    checked(buffers.DictionaryCount * Unsafe.SizeOf<BinaryValueDescriptor>()))
                : buffers.BorrowedBinaryDictionaryPayload.Length;
            payloadBytes = Math.Max(1, dictionaryPayloadBytes / buffers.DictionaryCount);
        }
        return Unsafe.SizeOf<BinaryValueDescriptor>() +
            Math.Min(payloadBytes, DecodeBatchSizeBytes);
    }

    internal static ColumnBuffer<T> DecodeNextEncodedBatch<T>(ReadOnlySpan<byte> payload,
        Column column, ref ColumnReadBuffers<T> buffers, IParquetBufferPool bufferPool,
        ref EncodedPageState page)
    {
        System.Diagnostics.Debug.Assert(page.Active);
        var dataPayload = payload.Slice(page.DataOffset, page.DataLength);
        if (page.DecoderKind == EncodedDecoderKind.PlainBinary &&
            page.BorrowedDataPayload.IsEmpty &&
            column.PhysicalType == ParquetPhysicalType.ByteArray)
            return DecodeNextOwnedPlainBinaryBatch(dataPayload, ref buffers, bufferPool, ref page);
        var targetCount = Math.Max(1, DecodeBatchSizeBytes / page.BatchElementSize);
        if (page.DefinitionBitsetLength == 0 &&
            page.DecoderKind == EncodedDecoderKind.Dictionary && page.IsNullable &&
            column.Converter is null && column.PhysicalType == ParquetPhysicalType.Int64 &&
            typeof(T) == typeof(long?) && NullableInt64HasCanonicalLayout &&
            Avx2.IsSupported && Bmi2.X64.IsSupported && page.Rle.BitWidth is >= 1 and <= 4)
        {
            var remaining = page.ValueCount - page.ValueOffset;
            var target = Math.Min(targetCount, remaining);
            var capacity = Math.Min(checked(target + 7), remaining);
            var values = buffers.GetValues<T>(capacity, bufferPool);
            var decoded = DecodeNullableInt64DictionaryNarrowRleBatch(dataPayload,
                buffers.GetDictionary<long>(), Unsafe.As<Span<T>, Span<long?>>(ref values),
                target, ref page.Rle);
            page.ValueOffset += decoded;
            page.PhysicalOffset += decoded;
            if (!page.Active && page.PhysicalOffset != page.PhysicalCount)
                throw new CorruptParquetException(
                    $"Definition levels consumed {page.PhysicalOffset} physical values, expected {page.PhysicalCount}.");
            return buffers.CreateNativeBuffer(decoded);
        }

        var logicalTarget = Math.Min(targetCount, page.ValueCount - page.ValueOffset);
        var physicalTarget = page.DefinitionBitsetLength == 0
            ? logicalTarget
            : CountDefinitionBits(buffers.GetCompactDefinitions(page.DefinitionBitsetLength),
                page.ValueOffset, logicalTarget);

        var physicalBatchCount = physicalTarget == 0
            ? 0
            : PlanEncodedPhysicalBatch(dataPayload, physicalTarget, ref page);
        var logicalBatchCount = physicalBatchCount == physicalTarget
            ? logicalTarget
            : CountLogicalValuesThroughPhysical(
                buffers.GetCompactDefinitions(page.DefinitionBitsetLength), page.ValueOffset,
                page.ValueCount - page.ValueOffset, physicalBatchCount);

        ColumnBuffer<T> buffer;
        if (page.IsBinary)
            buffer = DecodeBinaryBatch(dataPayload, column, logicalBatchCount,
                physicalBatchCount, ref buffers, bufferPool, ref page);
        else
            buffer = DecodeNumericEncodedBatch(dataPayload, column, logicalBatchCount,
                physicalBatchCount, ref buffers, bufferPool, ref page);

        page.ValueOffset += logicalBatchCount;
        page.PhysicalOffset += physicalBatchCount;
        if (!page.Active && page.PhysicalOffset != page.PhysicalCount)
            throw new CorruptParquetException(
                $"Definition levels consumed {page.PhysicalOffset} physical values, expected {page.PhysicalCount}.");
        return buffer;
    }

    static int PlanEncodedPhysicalBatch(ReadOnlySpan<byte> payload, int targetCount,
        ref EncodedPageState page)
    {
        var remaining = page.PhysicalCount - page.PhysicalOffset;
        if (remaining <= targetCount)
            return remaining;
        return page.DecoderKind switch
        {
            EncodedDecoderKind.BooleanRle or EncodedDecoderKind.Dictionary
                => PlanRleBatch(payload, targetCount, page.Rle),
            EncodedDecoderKind.DeltaBinaryPacked or EncodedDecoderKind.DeltaLengthByteArray
                => page.Delta.NextBatchCount(targetCount),
            EncodedDecoderKind.DeltaByteArray
                => PlanDeltaPairBatch(targetCount, page.Delta, page.Delta2),
            EncodedDecoderKind.Alp => page.Alp.NextBatchCount(targetCount),
            _ => targetCount
        };
    }

    static int PlanDeltaPairBatch(int targetCount,
        DeltaBinaryPackedDecoder.BatchState first,
        DeltaBinaryPackedDecoder.BatchState second)
    {
        var remaining = first.ValueCount - first.ValuesRead;
        if (remaining <= targetCount)
            return remaining;
        var firstValue = first.ValuesRead == 0 ? 1 : 0;
        var commonBlockSize = LeastCommonMultiple(first.BlockSize, second.BlockSize);
        var valuesAfterFirst = Math.Max(0, targetCount - firstValue);
        var blocks = Math.Max(1L, (valuesAfterFirst + commonBlockSize - 1) / commonBlockSize);
        return (int)Math.Min(remaining, firstValue + blocks * commonBlockSize);
    }

    static long LeastCommonMultiple(int first, int second)
    {
        var a = first;
        var b = second;
        while (b != 0)
        {
            var remainder = a % b;
            a = b;
            b = remainder;
        }
        return (long)(first / a) * second;
    }

    static int CountDefinitionBits(ReadOnlySpan<byte> definitions, int valueOffset, int valueCount)
    {
        var count = 0;
        while (valueCount != 0 && (valueOffset & 7) != 0)
        {
            count += (definitions[valueOffset >> 3] >> (valueOffset & 7)) & 1;
            valueOffset++;
            valueCount--;
        }
        while (valueCount >= 8)
        {
            count += System.Numerics.BitOperations.PopCount(definitions[valueOffset >> 3]);
            valueOffset += 8;
            valueCount -= 8;
        }
        while (valueCount-- != 0)
        {
            count += (definitions[valueOffset >> 3] >> (valueOffset & 7)) & 1;
            valueOffset++;
        }
        return count;
    }

    static int CountLogicalValuesThroughPhysical(ReadOnlySpan<byte> definitions,
        int valueOffset, int remainingValueCount, int physicalCount)
    {
        if (definitions.IsEmpty)
            return physicalCount;
        var logicalCount = 0;
        while (remainingValueCount != 0 && (valueOffset & 7) != 0)
        {
            physicalCount -= (definitions[valueOffset >> 3] >> (valueOffset & 7)) & 1;
            valueOffset++;
            logicalCount++;
            remainingValueCount--;
            if (physicalCount == 0)
                return logicalCount;
        }
        while (remainingValueCount >= 8)
        {
            var present = System.Numerics.BitOperations.PopCount(definitions[valueOffset >> 3]);
            if (present >= physicalCount)
                break;
            physicalCount -= present;
            valueOffset += 8;
            logicalCount += 8;
            remainingValueCount -= 8;
        }
        while (remainingValueCount-- != 0)
        {
            physicalCount -= (definitions[valueOffset >> 3] >> (valueOffset & 7)) & 1;
            valueOffset++;
            logicalCount++;
            if (physicalCount == 0)
                return logicalCount;
        }
        throw new CorruptParquetException(
            $"Definition levels contain fewer than {physicalCount} remaining physical values.");
    }

    static ColumnBuffer<T> DecodeNumericEncodedBatch<T>(ReadOnlySpan<byte> payload, Column column,
        int logicalCount, int physicalCount, ref ColumnReadBuffers<T> buffers,
        IParquetBufferPool bufferPool, ref EncodedPageState page)
    {
        var values = buffers.GetValues<T>(logicalCount, bufferPool);
        if (physicalCount == 0)
        {
            values.Clear();
            return buffers.CreateNativeBuffer(logicalCount);
        }

        ReadOnlySpan<byte> byteDefinitions = [];
        ReadOnlySpan<int> intDefinitions = [];
        if (page.IsNullable && column.Converter is not null)
        {
            var definitions = buffers.GetExpandedDefinitions(logicalCount, bufferPool);
            if (page.DefinitionBitsetLength == 0)
                definitions.Fill(1);
            else
                ExpandDefinitionBitset(buffers.GetCompactDefinitions(page.DefinitionBitsetLength),
                    page.ValueOffset, definitions, out _);
            intDefinitions = definitions;
        }
        else if (page.IsNullable)
        {
            if (page.DefinitionBitsetLength != 0)
            {
                var definitions = AsBytes(values)[..logicalCount];
                ExpandDefinitionBitset(buffers.GetCompactDefinitions(page.DefinitionBitsetLength),
                    page.ValueOffset, definitions, out _);
                byteDefinitions = definitions;
            }
        }

        DecodeEncodedValues(payload, column, byteDefinitions, intDefinitions, physicalCount,
            values, ref page, ref buffers, bufferPool);
        return buffers.CreateNativeBuffer(logicalCount);
    }

    static void DecodeEncodedValues<T>(ReadOnlySpan<byte> payload, Column column,
        ReadOnlySpan<byte> byteDefinitions, ReadOnlySpan<int> intDefinitions,
        int physicalCount, Span<T> values, ref EncodedPageState page,
        ref ColumnReadBuffers<T> buffers, IParquetBufferPool bufferPool)
    {
        var physicalType = GetPhysicalDecodeType<T>(column);
        if (physicalType == typeof(int))
            DecodeEncodedValues<T, int>(payload, column, byteDefinitions, intDefinitions,
                physicalCount, values, ref page, ref buffers, bufferPool);
        else if (physicalType == typeof(long))
            DecodeEncodedValues<T, long>(payload, column, byteDefinitions, intDefinitions,
                physicalCount, values, ref page, ref buffers, bufferPool);
        else if (physicalType == typeof(bool))
            DecodeEncodedValues<T, bool>(payload, column, byteDefinitions, intDefinitions,
                physicalCount, values, ref page, ref buffers, bufferPool);
        else if (physicalType == typeof(float))
            DecodeEncodedValues<T, float>(payload, column, byteDefinitions, intDefinitions,
                physicalCount, values, ref page, ref buffers, bufferPool);
        else if (physicalType == typeof(double))
            DecodeEncodedValues<T, double>(payload, column, byteDefinitions, intDefinitions,
                physicalCount, values, ref page, ref buffers, bufferPool);
        else if (physicalType == typeof(decimal))
            DecodeEncodedValues<T, decimal>(payload, column, byteDefinitions, intDefinitions,
                physicalCount, values, ref page, ref buffers, bufferPool);
        else if (physicalType == typeof(byte))
            DecodeEncodedValues<T, byte>(payload, column, byteDefinitions, intDefinitions,
                physicalCount, values, ref page, ref buffers, bufferPool);
        else if (physicalType == typeof(ushort))
            DecodeEncodedValues<T, ushort>(payload, column, byteDefinitions, intDefinitions,
                physicalCount, values, ref page, ref buffers, bufferPool);
        else if (physicalType == typeof(uint))
            DecodeEncodedValues<T, uint>(payload, column, byteDefinitions, intDefinitions,
                physicalCount, values, ref page, ref buffers, bufferPool);
        else if (physicalType == typeof(ulong))
            DecodeEncodedValues<T, ulong>(payload, column, byteDefinitions, intDefinitions,
                physicalCount, values, ref page, ref buffers, bufferPool);
        else if (physicalType == typeof(DateOnly))
            DecodeEncodedValues<T, DateOnly>(payload, column, byteDefinitions, intDefinitions,
                physicalCount, values, ref page, ref buffers, bufferPool);
        else if (physicalType == typeof(DateTime))
            DecodeEncodedValues<T, DateTime>(payload, column, byteDefinitions, intDefinitions,
                physicalCount, values, ref page, ref buffers, bufferPool);
        else if (physicalType == typeof(DateTimeOffset))
            DecodeEncodedValues<T, DateTimeOffset>(payload, column, byteDefinitions, intDefinitions,
                physicalCount, values, ref page, ref buffers, bufferPool);
        else if (physicalType == typeof(TimeOnly))
            DecodeEncodedValues<T, TimeOnly>(payload, column, byteDefinitions, intDefinitions,
                physicalCount, values, ref page, ref buffers, bufferPool);
        else if (physicalType == typeof(Guid))
            DecodeEncodedValues<T, Guid>(payload, column, byteDefinitions, intDefinitions,
                physicalCount, values, ref page, ref buffers, bufferPool);
        else
            throw new InvalidOperationException($"Unsupported encoded projection '{typeof(T)}'.");
    }

    static void DecodeEncodedValues<T, TValue>(ReadOnlySpan<byte> payload, Column column,
        ReadOnlySpan<byte> byteDefinitions, ReadOnlySpan<int> intDefinitions,
        int physicalCount, Span<T> values, ref EncodedPageState page,
        ref ColumnReadBuffers<T> buffers, IParquetBufferPool bufferPool)
        where TValue : struct
    {
        var converter = column.Converter;
        if (converter is null && typeof(T) == typeof(TValue))
        {
            DecodeEncodedPhysical(payload, column,
                Unsafe.As<Span<T>, Span<TValue>>(ref values), ref page, ref buffers, bufferPool);
            return;
        }

        if (converter is null && byteDefinitions.IsEmpty && physicalCount == values.Length)
        {
            if (page.DecoderKind == EncodedDecoderKind.DeltaBinaryPacked &&
                typeof(T) == typeof(int?) && typeof(TValue) == typeof(int))
            {
                DeltaBinaryPackedDecoder.ReadNullableInt32Batch(payload,
                    Unsafe.As<Span<T>, Span<int?>>(ref values), ref page.Delta,
                    NullableInt32HasCanonicalLayout);
                return;
            }

            if (page.DecoderKind == EncodedDecoderKind.Dictionary &&
                typeof(T) == typeof(int?) && typeof(TValue) == typeof(int))
            {
                var dictionary = buffers.GetDictionary<int>();
                var nullable = Unsafe.As<Span<T>, Span<int?>>(ref values);
                if (NullableInt32HasCanonicalLayout && Avx2.IsSupported &&
                    Bmi2.X64.IsSupported && page.Rle.BitWidth is 1 or 2)
                    DecodeNullableInt32DictionaryNarrowRleBatch(payload, dictionary,
                        nullable, ref page.Rle);
                else
                    DecodeNullableInt32DictionaryRleBatch(payload, dictionary,
                        nullable, ref page.Rle);
                return;
            }

            if (page.DecoderKind == EncodedDecoderKind.DeltaBinaryPacked &&
                typeof(T) == typeof(long?) && typeof(TValue) == typeof(long))
            {
                var nullable = Unsafe.As<Span<T>, Span<long?>>(ref values);
                var raw = MemoryMarshal.Cast<byte, long>(AsBytes(nullable))[..physicalCount];
                DeltaBinaryPackedDecoder.ReadInt64Batch(payload, raw, ref page.Delta);
                ExpandAllPresentInt64Batch(raw, nullable);
                return;
            }

            if (page.DecoderKind == EncodedDecoderKind.DeltaBinaryPacked &&
                typeof(T) == typeof(DateTime?) && typeof(TValue) == typeof(DateTime))
            {
                var nullable = Unsafe.As<Span<T>, Span<DateTime?>>(ref values);
                var raw = MemoryMarshal.Cast<byte, long>(AsBytes(nullable))[..physicalCount];
                DeltaBinaryPackedDecoder.ReadInt64Batch(payload, raw, ref page.Delta);
                var timestamp = GetTimestampLogicalType(column.LogicalType);
                var kind = timestamp.IsAdjustedToUtc
                    ? DateTimeKind.Utc
                    : DateTimeKind.Unspecified;
                MaterializeAllPresentNullableDateTimes(raw, nullable, timestamp.Unit, kind);
                return;
            }

            var allPresentByteLength = checked(physicalCount * Unsafe.SizeOf<TValue>());
            var allPresent = MemoryMarshal.Cast<byte, TValue>(
                buffers.GetScratch(allPresentByteLength, bufferPool));
            DecodeEncodedPhysical(payload, column, allPresent, ref page, ref buffers, bufferPool);
            ExpandAllPresentEncodedBatch(allPresent,
                Unsafe.As<Span<T>, Span<TValue?>>(ref values));
            return;
        }

        var physicalByteLength = checked(physicalCount * Unsafe.SizeOf<TValue>());
        var physical = MemoryMarshal.Cast<byte, TValue>(
            buffers.GetScratch(physicalByteLength, bufferPool));
        DecodeEncodedPhysical(payload, column, physical, ref page, ref buffers, bufferPool);
        if (converter is not null)
        {
            if (intDefinitions.IsEmpty)
                converter.ConvertFromPhysical(MemoryMarshal.AsBytes(physical), AsBytes(values), physicalCount);
            else
                converter.ConvertNullableFromPhysical(MemoryMarshal.AsBytes(physical), intDefinitions,
                    AsBytes(values), physicalCount);
            return;
        }

        ScatterNullableFixedWidthBatch(byteDefinitions, physical,
            Unsafe.As<Span<T>, Span<TValue?>>(ref values));
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    static void ExpandAllPresentEncodedBatch<TValue>(ReadOnlySpan<TValue> source,
        Span<TValue?> destination)
        where TValue : struct
    {
        if (typeof(TValue) == typeof(int))
        {
            ExpandAllPresentInt32Batch(
                Unsafe.As<ReadOnlySpan<TValue>, ReadOnlySpan<int>>(ref source),
                Unsafe.As<Span<TValue?>, Span<int?>>(ref destination));
            return;
        }
        if (typeof(TValue) == typeof(long))
        {
            ExpandAllPresentInt64Batch(
                Unsafe.As<ReadOnlySpan<TValue>, ReadOnlySpan<long>>(ref source),
                Unsafe.As<Span<TValue?>, Span<long?>>(ref destination));
            return;
        }
        if (typeof(TValue) == typeof(DateTime))
        {
            ExpandAllPresentDateTimeBatch(
                Unsafe.As<ReadOnlySpan<TValue>, ReadOnlySpan<DateTime>>(ref source),
                Unsafe.As<Span<TValue?>, Span<DateTime?>>(ref destination));
            return;
        }
        for (var i = 0; i < destination.Length; i++)
            destination[i] = source[i];
    }

    static void DecodeEncodedPhysical<TPage, TValue>(ReadOnlySpan<byte> payload, Column column,
        Span<TValue> destination, ref EncodedPageState page,
        ref ColumnReadBuffers<TPage> buffers, IParquetBufferPool bufferPool)
        where TValue : struct
    {
        switch (page.DecoderKind)
        {
            case EncodedDecoderKind.BooleanRle when typeof(TValue) == typeof(bool):
                DecodeBooleanRleBatch(payload,
                    Unsafe.As<Span<TValue>, Span<bool>>(ref destination), ref page.Rle);
                return;
            case EncodedDecoderKind.Dictionary:
                DecodeDictionaryRleBatch(payload, buffers.GetDictionary<TValue>(), destination,
                    ref page.Rle);
                return;
            case EncodedDecoderKind.DeltaBinaryPacked:
                DecodeDeltaBatch(payload, column, destination, ref page.Delta,
                    ref buffers, bufferPool);
                return;
            case EncodedDecoderKind.Alp:
                if (!AlpDecoder.TryDecodeBatch(payload, column, destination, ref page.Alp))
                    throw new InvalidOperationException(
                        $"ALP decoding declined fixed-width type '{typeof(TValue)}'.");
                return;
            case EncodedDecoderKind.DeltaByteArray:
                DecodeFixedDeltaByteArrayBatch(payload, column, destination, ref page,
                    ref buffers, bufferPool);
                return;
            default:
                throw new InvalidOperationException(
                    $"Unsupported encoded decoder kind '{page.DecoderKind}'.");
        }
    }

    static void DecodeDeltaBatch<TPage, TValue>(ReadOnlySpan<byte> payload, Column column,
        Span<TValue> destination, ref DeltaBinaryPackedDecoder.BatchState state,
        ref ColumnReadBuffers<TPage> buffers, IParquetBufferPool bufferPool)
        where TValue : struct
    {
        if (column.PhysicalType == ParquetPhysicalType.Int32)
        {
            Span<int> raw;
            if (Unsafe.SizeOf<TValue>() >= sizeof(int))
                raw = MemoryMarshal.Cast<TValue, int>(destination)[..destination.Length];
            else
                raw = buffers.GetExpandedDefinitions(destination.Length, bufferPool);
            DeltaBinaryPackedDecoder.ReadInt32Batch(payload, raw, ref state);
            if (typeof(TValue) == typeof(int) || typeof(TValue) == typeof(uint))
                return;
            for (var i = destination.Length - 1; i >= 0; i--)
            {
                if (typeof(TValue) == typeof(byte))
                    Unsafe.As<Span<TValue>, Span<byte>>(ref destination)[i] = unchecked((byte)raw[i]);
                else if (typeof(TValue) == typeof(ushort))
                    Unsafe.As<Span<TValue>, Span<ushort>>(ref destination)[i] = unchecked((ushort)raw[i]);
                else if (typeof(TValue) == typeof(decimal))
                    Unsafe.As<Span<TValue>, Span<decimal>>(ref destination)[i] =
                        ParquetDecimalConverter.FromInt32(raw[i], column);
                else if (typeof(TValue) == typeof(DateOnly))
                    Unsafe.As<Span<TValue>, Span<DateOnly>>(ref destination)[i] = DecodeDate(raw[i]);
                else if (typeof(TValue) == typeof(TimeOnly))
                    Unsafe.As<Span<TValue>, Span<TimeOnly>>(ref destination)[i] =
                        DecodeTime(raw[i], column.LogicalType);
                else
                    throw new InvalidOperationException(
                        $"Delta Int32 decoding declined '{typeof(TValue)}'.");
            }
            return;
        }

        if (column.PhysicalType != ParquetPhysicalType.Int64)
            throw new InvalidOperationException(
                $"Delta decoding does not support physical type '{column.PhysicalType}'.");
        var raw64 = MemoryMarshal.Cast<TValue, long>(destination)[..destination.Length];
        DeltaBinaryPackedDecoder.ReadInt64Batch(payload, raw64, ref state);
        if (typeof(TValue) == typeof(long) || typeof(TValue) == typeof(ulong))
            return;

        if (typeof(TValue) == typeof(decimal))
        {
            var typed = Unsafe.As<Span<TValue>, Span<decimal>>(ref destination);
            for (var i = destination.Length - 1; i >= 0; i--)
                typed[i] = ParquetDecimalConverter.FromInt64(raw64[i], column);
            return;
        }
        if (typeof(TValue) == typeof(TimeOnly))
        {
            var typed = Unsafe.As<Span<TValue>, Span<TimeOnly>>(ref destination);
            for (var i = destination.Length - 1; i >= 0; i--)
                typed[i] = DecodeTime(raw64[i], column.LogicalType);
            return;
        }
        if (typeof(TValue) == typeof(DateTime))
        {
            var timestamp = GetTimestampLogicalType(column.LogicalType);
            var kind = timestamp.IsAdjustedToUtc ? DateTimeKind.Utc : DateTimeKind.Unspecified;
            var typed = Unsafe.As<Span<TValue>, Span<DateTime>>(ref destination);
            if (timestamp.Unit == TimeUnit.Micros)
            {
                // These bounds include the epoch offset, so valid values need no
                // separate scaling-overflow check. Keep the general helper and its
                // exception construction off the per-value hot path.
                const long minimumMicroseconds = -62_135_596_800_000_000;
                const long maximumMicroseconds = 253_402_300_799_999_999;
                for (var i = destination.Length - 1; i >= 0; i--)
                {
                    var raw = raw64[i];
                    if (raw < minimumMicroseconds || raw > maximumMicroseconds)
                        _ = TimestampTicks(raw, TimeUnit.Micros);
                    typed[i] = new DateTime(raw * 10 + DateTime.UnixEpoch.Ticks, kind);
                }
            }
            else
            {
                for (var i = destination.Length - 1; i >= 0; i--)
                    typed[i] = new DateTime(TimestampTicks(raw64[i], timestamp.Unit), kind);
            }
            return;
        }
        if (typeof(TValue) == typeof(DateTimeOffset))
        {
            var typed = Unsafe.As<Span<TValue>, Span<DateTimeOffset>>(ref destination);
            for (var i = destination.Length - 1; i >= 0; i--)
                typed[i] = DecodeTimestamp(raw64[i], column.LogicalType);
            return;
        }
        throw new InvalidOperationException(
            $"Delta Int64 decoding declined '{typeof(TValue)}'.");
    }

    static RleBatchState StartDictionaryRleBatch(ReadOnlySpan<byte> payload, int valueCount)
    {
        if (valueCount != 0 && payload.IsEmpty)
            throw new CorruptParquetException(
                "Dictionary payload is empty but value count is non-zero.");
        var bitWidth = payload.IsEmpty ? 0 : payload[0];
        if (bitWidth > 32)
            throw new CorruptParquetException(
                $"Dictionary bit width {bitWidth} exceeds the maximum of 32.");
        return StartRleBatch(payload, valueCount, bitWidth, payload.IsEmpty ? 0 : 1);
    }

    static RleBatchState StartRleBatch(ReadOnlySpan<byte> payload, int valueCount,
        int bitWidth, int initialOffset)
        => new()
        {
            Offset = initialOffset,
            ValueCount = valueCount,
            BitWidth = bitWidth
        };

    static int PlanRleBatch(ReadOnlySpan<byte> payload, int targetCount, RleBatchState state)
    {
        var destination = targetCount;
        var produced = 0;
        while (produced < destination && state.ValuesRead < state.ValueCount)
        {
            EnsureRleRun(payload, ref state);
            var pageRemaining = state.ValueCount - state.ValuesRead;
            if (!state.LiteralRun)
            {
                var count = (int)Math.Min(state.RunRemaining,
                    checked((uint)Math.Min(destination - produced, pageRemaining)));
                state.RunRemaining -= checked((uint)count);
                state.ValuesRead += count;
                produced += count;
                continue;
            }

            var needed = Math.Min(destination - produced, pageRemaining);
            var groups = Math.Max(1, (needed + 7) / 8);
            var encodedValues = checked(groups * 8);
            var countToConsume = Math.Min(checked((uint)encodedValues), state.RunRemaining);
            var countToExpose = Math.Min(checked((int)countToConsume), pageRemaining);
            var bytes = checked((int)(countToConsume / 8) * state.BitWidth);
            RequireRleBytes(payload, state.Offset, bytes);
            state.Offset += bytes;
            state.RunRemaining -= countToConsume;
            state.ValuesRead += countToExpose;
            produced += countToExpose;
        }
        return produced;
    }

    static void DecodeBooleanRleBatch(ReadOnlySpan<byte> payload, Span<bool> destination,
        ref RleBatchState state)
    {
        var written = 0;
        while (written < destination.Length)
        {
            EnsureRleRun(payload, ref state);
            var pageRemaining = state.ValueCount - state.ValuesRead;
            if (!state.LiteralRun)
            {
                var count = (int)Math.Min(state.RunRemaining,
                    checked((uint)Math.Min(destination.Length - written, pageRemaining)));
                destination.Slice(written, count).Fill(state.RepeatedValue != 0);
                state.RunRemaining -= checked((uint)count);
                state.ValuesRead += count;
                written += count;
                continue;
            }

            var remaining = Math.Min(destination.Length - written, pageRemaining);
            var groups = Math.Max(1, (remaining + 7) / 8);
            var encodedValues = checked(groups * 8);
            var countToConsume = Math.Min(checked((uint)encodedValues), state.RunRemaining);
            var countToExpose = Math.Min(checked((int)countToConsume), remaining);
            var bytes = checked((int)(countToConsume / 8) * state.BitWidth);
            RequireRleBytes(payload, state.Offset, bytes);
            BooleanBitUnpacker.Unpack(payload.Slice(state.Offset, bytes), 0,
                destination.Slice(written, countToExpose));
            state.Offset += bytes;
            state.RunRemaining -= countToConsume;
            state.ValuesRead += countToExpose;
            written += countToExpose;
        }
    }

    static void DecodeDictionaryRleBatch<T>(ReadOnlySpan<byte> payload,
        ReadOnlySpan<T> dictionary, Span<T> destination, ref RleBatchState state)
    {
        var written = 0;
        while (written < destination.Length)
        {
            EnsureRleRun(payload, ref state);
            var pageRemaining = state.ValueCount - state.ValuesRead;
            if (!state.LiteralRun)
            {
                ValidateDictionaryIndex(state.RepeatedValue, dictionary.Length);
                var count = (int)Math.Min(state.RunRemaining,
                    checked((uint)Math.Min(destination.Length - written, pageRemaining)));
                destination.Slice(written, count).Fill(dictionary[state.RepeatedValue]);
                state.RunRemaining -= checked((uint)count);
                state.ValuesRead += count;
                written += count;
                continue;
            }

            var remaining = Math.Min(destination.Length - written, pageRemaining);
            var groups = Math.Max(1, (remaining + 7) / 8);
            var encodedValues = checked(groups * 8);
            var countToConsume = Math.Min(checked((uint)encodedValues), state.RunRemaining);
            var countToExpose = Math.Min(checked((int)countToConsume), remaining);
            var bytes = checked((int)(countToConsume / 8) * state.BitWidth);
            RequireRleBytes(payload, state.Offset, bytes);
            DecodeDictionaryLiteralIndexes(payload.Slice(state.Offset, bytes), state.BitWidth,
                dictionary, destination.Slice(written, countToExpose));
            state.Offset += bytes;
            state.RunRemaining -= countToConsume;
            state.ValuesRead += countToExpose;
            written += countToExpose;
        }
    }

    static void DecodeNullableInt32DictionaryRleBatch(ReadOnlySpan<byte> payload,
        ReadOnlySpan<int> dictionary, Span<int?> destination, ref RleBatchState state)
    {
        var written = 0;
        while (written < destination.Length)
        {
            EnsureRleRun(payload, ref state);
            var pageRemaining = state.ValueCount - state.ValuesRead;
            if (!state.LiteralRun)
            {
                ValidateDictionaryIndex(state.RepeatedValue, dictionary.Length);
                var count = (int)Math.Min(state.RunRemaining,
                    checked((uint)Math.Min(destination.Length - written, pageRemaining)));
                destination.Slice(written, count).Fill(dictionary[state.RepeatedValue]);
                state.RunRemaining -= checked((uint)count);
                state.ValuesRead += count;
                written += count;
                continue;
            }

            var remaining = Math.Min(destination.Length - written, pageRemaining);
            var groups = Math.Max(1, (remaining + 7) / 8);
            var encodedValues = checked(groups * 8);
            var countToConsume = Math.Min(checked((uint)encodedValues), state.RunRemaining);
            var countToExpose = Math.Min(checked((int)countToConsume), remaining);
            var bytes = checked((int)(countToConsume / 8) * state.BitWidth);
            RequireRleBytes(payload, state.Offset, bytes);
            DecodeNullableInt32DictionaryLiteral(payload.Slice(state.Offset, bytes),
                state.BitWidth, dictionary, destination.Slice(written, countToExpose));
            state.Offset += bytes;
            state.RunRemaining -= countToConsume;
            state.ValuesRead += countToExpose;
            written += countToExpose;
        }
    }

    static void DecodeNullableInt32DictionaryNarrowRleBatch(ReadOnlySpan<byte> payload,
        ReadOnlySpan<int> dictionary, Span<int?> destination, ref RleBatchState state)
    {
        var written = 0;
        while (written < destination.Length)
        {
            EnsureRleRun(payload, ref state);
            var pageRemaining = state.ValueCount - state.ValuesRead;
            if (!state.LiteralRun)
            {
                ValidateDictionaryIndex(state.RepeatedValue, dictionary.Length);
                var count = (int)Math.Min(state.RunRemaining,
                    checked((uint)Math.Min(destination.Length - written, pageRemaining)));
                destination.Slice(written, count).Fill(dictionary[state.RepeatedValue]);
                state.RunRemaining -= checked((uint)count);
                state.ValuesRead += count;
                written += count;
                continue;
            }

            var remaining = Math.Min(destination.Length - written, pageRemaining);
            var groups = Math.Max(1, (remaining + 7) / 8);
            var encodedValues = checked(groups * 8);
            var countToConsume = Math.Min(checked((uint)encodedValues), state.RunRemaining);
            var countToExpose = Math.Min(checked((int)countToConsume), remaining);
            var bytes = checked((int)(countToConsume / 8) * state.BitWidth);
            RequireRleBytes(payload, state.Offset, bytes);
            var literal = payload.Slice(state.Offset, bytes);
            var vectorizedLength = countToExpose & ~7;
            var target = destination.Slice(written, countToExpose);
            if (state.BitWidth == 1)
                DecodeNullableInt32DictionaryBits(literal, dictionary,
                    target[..vectorizedLength]);
            else
                DecodeNullableInt32DictionaryPairs(literal, dictionary,
                    target[..vectorizedLength]);
            if (vectorizedLength != countToExpose)
            {
                var vectorBytes = vectorizedLength / 8 * state.BitWidth;
                DecodeNullableInt32DictionaryLiteral(literal[vectorBytes..], state.BitWidth,
                    dictionary, target[vectorizedLength..]);
            }
            state.Offset += bytes;
            state.RunRemaining -= countToConsume;
            state.ValuesRead += countToExpose;
            written += countToExpose;
        }
    }

    static int DecodeNullableInt64DictionaryNarrowRleBatch(ReadOnlySpan<byte> payload,
        ReadOnlySpan<long> dictionary, Span<long?> destination, int targetCount,
        ref RleBatchState state)
    {
        var written = 0;
        while (written < targetCount)
        {
            EnsureRleRun(payload, ref state);
            var pageRemaining = state.ValueCount - state.ValuesRead;
            if (!state.LiteralRun)
            {
                ValidateDictionaryIndex(state.RepeatedValue, dictionary.Length);
                var count = (int)Math.Min(state.RunRemaining,
                    checked((uint)Math.Min(targetCount - written, pageRemaining)));
                FillNullableInt64(destination.Slice(written, count),
                    dictionary[state.RepeatedValue]);
                state.RunRemaining -= checked((uint)count);
                state.ValuesRead += count;
                written += count;
                continue;
            }

            var remaining = Math.Min(targetCount - written, pageRemaining);
            var groups = Math.Max(1, (remaining + 7) / 8);
            var encodedValues = checked(groups * 8);
            var countToConsume = Math.Min(checked((uint)encodedValues), state.RunRemaining);
            var countToExpose = Math.Min(checked((int)countToConsume), pageRemaining);
            var bytes = checked((int)(countToConsume / 8) * state.BitWidth);
            RequireRleBytes(payload, state.Offset, bytes);
            var literal = payload.Slice(state.Offset, bytes);
            var vectorizedLength = countToExpose & ~7;
            var target = destination.Slice(written, countToExpose);
            DecodeNullableInt64DictionarySmall(literal, state.BitWidth, dictionary,
                target[..vectorizedLength]);
            if (vectorizedLength != countToExpose)
            {
                var vectorBytes = vectorizedLength / 8 * state.BitWidth;
                DecodeNullableInt64DictionaryScalar(literal[vectorBytes..], state.BitWidth,
                    dictionary, target[vectorizedLength..]);
            }
            state.Offset += bytes;
            state.RunRemaining -= countToConsume;
            state.ValuesRead += countToExpose;
            written += countToExpose;
        }
        return written;
    }

    static void FillNullableInt64(Span<long?> destination, long value)
    {
        ref var target = ref Unsafe.As<long?, long>(ref MemoryMarshal.GetReference(destination));
        var packed = Vector256.Create(1L, value, 1L, value);
        var index = 0;
        for (; index <= destination.Length - 2; index += 2)
            packed.StoreUnsafe(ref target, (nuint)(index * 2));
        if (index != destination.Length)
            destination[index] = value;
    }

    static unsafe void DecodeNullableInt64DictionarySmall(ReadOnlySpan<byte> payload,
        int bitWidth, ReadOnlySpan<long> dictionary, Span<long?> destination)
    {
        ref var source = ref MemoryMarshal.GetReference(payload);
        ref var target = ref Unsafe.As<long?, long>(ref MemoryMarshal.GetReference(destination));
        var maximumIndex = Vector256.Create(dictionary.Length - 1);
        var laneValue = (1UL << bitWidth) - 1UL;
        var laneMask = laneValue | laneValue << 16 | laneValue << 32 | laneValue << 48;
        var present = Vector256.Create(1L);
        fixed (long* dictionaryPointer = dictionary)
        {
            var byteIndex = 0;
            for (var valueIndex = 0; valueIndex < destination.Length;
                 valueIndex += 8, byteIndex += bitWidth)
            {
                var packed = bitWidth switch
                {
                    1 => Unsafe.Add(ref source, byteIndex),
                    2 => Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref source, byteIndex)),
                    3 => (uint)(Unsafe.Add(ref source, byteIndex) |
                        Unsafe.Add(ref source, byteIndex + 1) << 8 |
                        Unsafe.Add(ref source, byteIndex + 2) << 16),
                    _ => Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref source, byteIndex))
                };
                var indexes = Avx2.ConvertToVector256Int32(Vector128.Create(
                    Bmi2.X64.ParallelBitDeposit(packed, laneMask),
                    Bmi2.X64.ParallelBitDeposit(packed >> (bitWidth * 4), laneMask)).AsUInt16());
                if (Avx2.MoveMask(Avx2.CompareGreaterThan(indexes, maximumIndex).AsByte()) != 0)
                {
                    for (var lane = 0; lane < 8; lane++)
                        ValidateDictionaryIndex(indexes.GetElement(lane), dictionary.Length);
                }

                StoreNullableInt64Values(
                    Avx2.GatherVector256(dictionaryPointer, indexes.GetLower(), sizeof(long)),
                    present, ref target, valueIndex * 2);
                StoreNullableInt64Values(
                    Avx2.GatherVector256(dictionaryPointer, indexes.GetUpper(), sizeof(long)),
                    present, ref target, (valueIndex + 4) * 2);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void StoreNullableInt64Values(Vector256<long> values, Vector256<long> present,
        ref long destination, int wordOffset)
    {
        var even = Avx2.UnpackLow(present, values);
        var odd = Avx2.UnpackHigh(present, values);
        Avx2.Permute2x128(even, odd, 0x20)
            .StoreUnsafe(ref destination, (nuint)wordOffset);
        Avx2.Permute2x128(even, odd, 0x31)
            .StoreUnsafe(ref destination, (nuint)(wordOffset + Vector256<long>.Count));
    }

    static void DecodeNullableInt64DictionaryScalar(ReadOnlySpan<byte> payload,
        int bitWidth, ReadOnlySpan<long> dictionary, Span<long?> destination)
    {
        var mask = (1UL << bitWidth) - 1UL;
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
            ValidateDictionaryIndex(dictionaryIndex, dictionary.Length);
            destination[i] = dictionary[dictionaryIndex];
        }
    }

    static unsafe void DecodeNullableInt32DictionaryLiteral(ReadOnlySpan<byte> payload,
        int bitWidth, ReadOnlySpan<int> dictionary, Span<int?> destination)
    {
        if (bitWidth == 0)
        {
            ValidateDictionaryIndex(0, dictionary.Length);
            destination.Fill(dictionary[0]);
            return;
        }

        if (NullableInt32HasCanonicalLayout && Avx2.IsSupported &&
            bitWidth is 8 or 9 && destination.Length >= 8)
        {
            var vectorizedLength = destination.Length & ~7;
            if (bitWidth == 8)
                DecodeNullableInt32DictionaryBytes(payload, dictionary,
                    destination[..vectorizedLength]);
            else
                DecodeNullableInt32DictionaryNineBit(payload, dictionary,
                    destination[..vectorizedLength]);
            payload = payload[(vectorizedLength / 8 * bitWidth)..];
            destination = destination[vectorizedLength..];
            if (destination.IsEmpty)
                return;
        }

        var mask = bitWidth == 32 ? ulong.MaxValue : (1UL << bitWidth) - 1UL;
        ulong bitBuffer = 0;
        var bufferedBits = 0;
        var payloadIndex = 0;
        for (var i = 0; i < destination.Length; i++)
        {
            while (bufferedBits < bitWidth)
            {
                bitBuffer |= (ulong)payload[payloadIndex++] << bufferedBits;
                bufferedBits += 8;
            }
            var dictionaryIndex = (int)(bitBuffer & mask);
            bitBuffer >>= bitWidth;
            bufferedBits -= bitWidth;
            ValidateDictionaryIndex(dictionaryIndex, dictionary.Length);
            destination[i] = dictionary[dictionaryIndex];
        }
    }

    static unsafe void DecodeNullableInt32DictionaryBits(ReadOnlySpan<byte> payload,
        ReadOnlySpan<int> dictionary, Span<int?> destination)
    {
        ref var source = ref MemoryMarshal.GetReference(payload);
        ref var target = ref Unsafe.As<int?, ulong>(ref MemoryMarshal.GetReference(destination));
        var maximumIndex = Vector256.Create(dictionary.Length - 1);
        fixed (int* dictionaryPointer = dictionary)
        {
            for (var valueIndex = 0; valueIndex < destination.Length; valueIndex += 8)
            {
                var decodedIndexes = Avx2.ConvertToVector256Int32(Vector128.CreateScalar(
                    Bmi2.X64.ParallelBitDeposit(Unsafe.Add(ref source, valueIndex >> 3),
                        0x0101_0101_0101_0101UL)).AsByte());
                if (dictionary.Length != 2)
                    ValidateNullableInt32DictionaryIndexes(decodedIndexes, dictionary.Length,
                        maximumIndex);
                var values = Avx2.GatherVector256(dictionaryPointer, decodedIndexes, sizeof(int));
                StoreNullableInt32Values(values, ref target, valueIndex);
            }
        }
    }

    static unsafe void DecodeNullableInt32DictionaryPairs(ReadOnlySpan<byte> payload,
        ReadOnlySpan<int> dictionary, Span<int?> destination)
    {
        ref var source = ref MemoryMarshal.GetReference(payload);
        ref var target = ref Unsafe.As<int?, ulong>(ref MemoryMarshal.GetReference(destination));
        var maximumIndex = Vector256.Create(dictionary.Length - 1);
        fixed (int* dictionaryPointer = dictionary)
        {
            for (var valueIndex = 0; valueIndex < destination.Length; valueIndex += 8)
            {
                var packed = Unsafe.ReadUnaligned<ushort>(
                    ref Unsafe.Add(ref source, valueIndex >> 2));
                var decodedIndexes = Avx2.ConvertToVector256Int32(Vector128.CreateScalar(
                    Bmi2.X64.ParallelBitDeposit(packed,
                        0x0303_0303_0303_0303UL)).AsByte());
                ValidateNullableInt32DictionaryIndexes(decodedIndexes, dictionary.Length,
                    maximumIndex);
                var values = Avx2.GatherVector256(dictionaryPointer, decodedIndexes, sizeof(int));
                StoreNullableInt32Values(values, ref target, valueIndex);
            }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static unsafe void DecodeNullableInt32DictionaryNineBit(ReadOnlySpan<byte> payload,
        ReadOnlySpan<int> dictionary, Span<int?> destination)
    {
        ref var source = ref MemoryMarshal.GetReference(payload);
        ref var target = ref Unsafe.As<int?, ulong>(ref MemoryMarshal.GetReference(destination));
        var maximumIndex = Vector256.Create(dictionary.Length - 1);
        fixed (int* dictionaryPointer = dictionary)
        {
            for (int valueIndex = 0, byteIndex = 0; valueIndex < destination.Length;
                 valueIndex += 8, byteIndex += 9)
            {
                var lower = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteIndex));
                var high = Unsafe.Add(ref source, byteIndex + 8);
                var packed = Vector128.Create(lower, (ulong)high).AsByte();
                var shuffled = Avx2.Shuffle(Vector256.Create(packed, packed),
                    DictionaryNineBitShuffle).AsUInt32();
                var decodedIndexes = (Avx2.ShiftRightLogicalVariable(shuffled,
                    DictionaryNineBitShifts) & Vector256.Create(0x1FFU)).AsInt32();
                ValidateNullableInt32DictionaryIndexes(decodedIndexes, dictionary.Length,
                    maximumIndex);
                var values = Avx2.GatherVector256(dictionaryPointer, decodedIndexes, sizeof(int));
                StoreNullableInt32Values(values, ref target, valueIndex);
            }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static unsafe void DecodeNullableInt32DictionaryBytes(ReadOnlySpan<byte> indexes,
        ReadOnlySpan<int> dictionary, Span<int?> destination)
    {
        ref var source = ref MemoryMarshal.GetReference(indexes);
        ref var target = ref Unsafe.As<int?, ulong>(ref MemoryMarshal.GetReference(destination));
        var maximumIndex = Vector256.Create(dictionary.Length - 1);
        fixed (int* dictionaryPointer = dictionary)
        {
            for (var valueIndex = 0; valueIndex < destination.Length; valueIndex += 8)
            {
                var packed = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, valueIndex));
                var decodedIndexes = Avx2.ConvertToVector256Int32(
                    Vector128.CreateScalar(packed).AsByte());
                ValidateNullableInt32DictionaryIndexes(decodedIndexes, dictionary.Length,
                    maximumIndex);
                var values = Avx2.GatherVector256(dictionaryPointer, decodedIndexes, sizeof(int));
                StoreNullableInt32Values(values, ref target, valueIndex);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void ValidateNullableInt32DictionaryIndexes(Vector256<int> indexes,
        int dictionaryLength, Vector256<int> maximumIndex)
    {
        var invalid = Avx2.CompareGreaterThan(indexes, maximumIndex);
        if (Avx.TestZ(invalid, invalid))
            return;
        for (var lane = 0; lane < 8; lane++)
            ValidateDictionaryIndex(indexes.GetElement(lane), dictionaryLength);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void StoreNullableInt32Values(Vector256<int> values, ref ulong target, int valueIndex)
    {
        var present = Vector128.Create(1U);
        var lower = values.GetLower().AsUInt32();
        var upper = values.GetUpper().AsUInt32();
        Vector256.Create(Sse2.UnpackLow(present, lower), Sse2.UnpackHigh(present, lower))
            .AsUInt64().StoreUnsafe(ref target, (nuint)valueIndex);
        Vector256.Create(Sse2.UnpackLow(present, upper), Sse2.UnpackHigh(present, upper))
            .AsUInt64().StoreUnsafe(ref target, (nuint)(valueIndex + 4));
    }

    static void DecodeDictionaryRleIndexesBatch(ReadOnlySpan<byte> payload,
        int dictionaryLength, Span<int> destination, ref RleBatchState state)
    {
        var written = 0;
        while (written < destination.Length)
        {
            EnsureRleRun(payload, ref state);
            var pageRemaining = state.ValueCount - state.ValuesRead;
            if (!state.LiteralRun)
            {
                ValidateDictionaryIndex(state.RepeatedValue, dictionaryLength);
                var count = (int)Math.Min(state.RunRemaining,
                    checked((uint)Math.Min(destination.Length - written, pageRemaining)));
                destination.Slice(written, count).Fill(state.RepeatedValue);
                state.RunRemaining -= checked((uint)count);
                state.ValuesRead += count;
                written += count;
                continue;
            }

            var remaining = Math.Min(destination.Length - written, pageRemaining);
            var groups = Math.Max(1, (remaining + 7) / 8);
            var encodedValues = checked(groups * 8);
            var countToConsume = Math.Min(checked((uint)encodedValues), state.RunRemaining);
            var countToExpose = Math.Min(checked((int)countToConsume), remaining);
            var bytes = checked((int)(countToConsume / 8) * state.BitWidth);
            RequireRleBytes(payload, state.Offset, bytes);
            DecodeDictionaryLiteralIndexes(payload.Slice(state.Offset, bytes), state.BitWidth,
                dictionaryLength, destination.Slice(written, countToExpose));
            state.Offset += bytes;
            state.RunRemaining -= countToConsume;
            state.ValuesRead += countToExpose;
            written += countToExpose;
        }
    }

    static void EnsureRleRun(ReadOnlySpan<byte> payload, ref RleBatchState state)
    {
        if (state.RunRemaining != 0)
            return;
        var remaining = payload[state.Offset..];
        var before = remaining.Length;
        var header = ReadUnsignedVarInt(ref remaining);
        state.Offset += before - remaining.Length;
        state.LiteralRun = (header & 1U) != 0;
        var count = header >> 1;
        if (count == 0)
            throw new CorruptParquetException("RLE run length must be positive.");
        if (state.LiteralRun)
        {
            if (count > uint.MaxValue / 8)
                throw new CorruptParquetException(
                    $"RLE literal run group count {count} is too large.");
            state.RunRemaining = count * 8;
            return;
        }

        state.RunRemaining = count;
        var byteWidth = (state.BitWidth + 7) >> 3;
        remaining = payload[state.Offset..];
        before = remaining.Length;
        state.RepeatedValue = byteWidth == 0 ? 0 : ReadLittleEndian(ref remaining, byteWidth);
        state.Offset += before - remaining.Length;
    }

    static void RequireRleBytes(ReadOnlySpan<byte> payload, int offset, int byteCount)
    {
        if ((uint)offset > (uint)payload.Length || payload.Length - offset < byteCount)
            throw new CorruptParquetException(
                $"RLE literal group claims {byteCount} bytes but only " +
                $"{Math.Max(0, payload.Length - offset)} remain.");
    }

    static ColumnBuffer<T> DecodeBinaryBatch<T>(ReadOnlySpan<byte> payload, Column column,
        int logicalCount, int physicalCount, ref ColumnReadBuffers<T> buffers,
        IParquetBufferPool bufferPool, ref EncodedPageState page)
    {
        if (physicalCount == 0)
        {
            buffers.GetBinaryValues(logicalCount, 0, bufferPool, out _).Clear();
            return buffers.CreateNativeBuffer(logicalCount);
        }

        var definitionBits = page.IsNullable
            ? buffers.GetCompactDefinitions(page.DefinitionBitsetLength)
            : [];
        var definitions = new BinaryDefinitionCursor(definitionBits, page.ValueOffset,
            logicalCount);

        ReadOnlyMemory<byte> borrowedValues = default;
        switch (page.DecoderKind)
        {
            case EncodedDecoderKind.PlainBinary:
            {
                var remaining = payload[page.BinaryPayloadOffset..];
                if (!page.BorrowedDataPayload.IsEmpty &&
                    column.PhysicalType == ParquetPhysicalType.ByteArray)
                {
                    var borrowedEncodedLength = DecodeBorrowedPlainBinaryBatch(remaining,
                        ref definitions, logicalCount, physicalCount, ref buffers, bufferPool);
                    borrowedValues = page.BorrowedDataPayload.Slice(
                        page.BinaryPayloadOffset, borrowedEncodedLength);
                    page.BinaryPayloadOffset += borrowedEncodedLength;
                    break;
                }

                var encodedLength = GetPlainBinaryBatchEncodedLength(remaining, column, physicalCount);
                var encoded = remaining[..encodedLength];
                var borrowed = page.BorrowedDataPayload.IsEmpty
                    ? default
                    : page.BorrowedDataPayload.Slice(page.BinaryPayloadOffset, encodedLength);
                var lengths = MemoryMarshal.Cast<byte, int>(
                    buffers.GetScratch(checked(physicalCount * sizeof(int)), bufferPool));
                ReadOnlySpan<int> expandedDefinitions = [];
                if (!definitionBits.IsEmpty)
                {
                    var expanded = buffers.GetExpandedDefinitions(logicalCount, bufferPool);
                    ExpandDefinitionBitset(definitionBits, page.ValueOffset, expanded, out _);
                    expandedDefinitions = expanded;
                }
                DecodePlainBinaryValues(encoded, borrowed, expandedDefinitions, logicalCount,
                    physicalCount, column, lengths, ref buffers, bufferPool,
                    out borrowedValues);
                page.BinaryPayloadOffset += encodedLength;
                break;
            }
            case EncodedDecoderKind.ByteStreamSplitBinary:
                DecodeByteStreamSplitBinaryBatch(payload, ref definitions, logicalCount,
                    physicalCount, column, ref buffers, bufferPool, ref page);
                break;
            case EncodedDecoderKind.Dictionary:
            {
                var indexes = MemoryMarshal.Cast<byte, int>(
                    buffers.GetScratch(checked(physicalCount * sizeof(int)), bufferPool));
                DecodeDictionaryRleIndexesBatch(payload, buffers.DictionaryCount, indexes,
                    ref page.Rle);
                MaterializeBinaryDictionaryBatch(indexes, definitionBits, page.ValueOffset,
                    logicalCount, ref buffers, bufferPool, out borrowedValues);
                break;
            }
            case EncodedDecoderKind.DeltaLengthByteArray:
                DecodeDeltaLengthBinaryBatch(payload, definitionBits, logicalCount, physicalCount,
                    ref buffers, bufferPool, ref page, out borrowedValues);
                break;
            case EncodedDecoderKind.DeltaByteArray:
                DecodeDeltaByteArrayBinaryBatch(payload, ref definitions, logicalCount, physicalCount,
                    column, ref buffers, bufferPool, ref page);
                break;
            default:
                throw new InvalidOperationException(
                    $"Unsupported binary decoder kind '{page.DecoderKind}'.");
        }

        if (page.PhysicalOffset + physicalCount == page.PhysicalCount &&
            page.DecoderKind is EncodedDecoderKind.PlainBinary or
                EncodedDecoderKind.DeltaLengthByteArray or EncodedDecoderKind.DeltaByteArray &&
            page.BinaryDataOffset + page.BinaryPayloadOffset != payload.Length)
            throw new CorruptParquetException(
                $"{page.DecoderKind} payload contains " +
                $"{payload.Length - page.BinaryDataOffset - page.BinaryPayloadOffset} trailing bytes.");

        return borrowedValues.IsEmpty
            ? buffers.CreateNativeBuffer(logicalCount)
            : buffers.CreateBorrowedBinaryBuffer(logicalCount, borrowedValues);
    }

    static ColumnBuffer<T> DecodeNextOwnedPlainBinaryBatch<T>(ReadOnlySpan<byte> payload,
        ref ColumnReadBuffers<T> buffers, IParquetBufferPool bufferPool,
        ref EncodedPageState page)
    {
        var remainingLogical = page.ValueCount - page.ValueOffset;
        var descriptorCapacity = Math.Min(remainingLogical,
            Math.Max(1, DecodeBatchSizeBytes / page.BatchElementSize));
        var remainingPayload = payload[page.BinaryPayloadOffset..];
        var descriptorBytes = checked(descriptorCapacity *
            Unsafe.SizeOf<BinaryValueDescriptor>());
        var payloadCapacity = Math.Min(
            Math.Max(0, DecodeBatchSizeBytes - descriptorBytes), remainingPayload.Length);
        if (page.PhysicalOffset != page.PhysicalCount)
        {
            if (remainingPayload.Length < sizeof(int))
                throw new CorruptParquetException(
                    "Payload too short to read byte array length prefix.");
            var firstLength = BinaryPrimitives.ReadUInt32LittleEndian(remainingPayload);
            if (firstLength > int.MaxValue)
                throw new CorruptParquetException(
                    $"Byte array length {firstLength} exceeds the supported maximum of {int.MaxValue}.");
            if (firstLength > (uint)(remainingPayload.Length - sizeof(int)))
                throw new CorruptParquetException(
                    $"Byte array length {firstLength} exceeds remaining payload " +
                    $"({remainingPayload.Length - sizeof(int)} bytes).");
            payloadCapacity = Math.Max(payloadCapacity, (int)firstLength);
        }

        var destination = buffers.GetBinaryValues(descriptorCapacity, payloadCapacity,
            bufferPool, out var destinationPayload);
        var definitionBits = page.IsNullable
            ? buffers.GetCompactDefinitions(page.DefinitionBitsetLength)
            : [];
        if (!definitionBits.IsEmpty)
            destination.Clear();

        var logicalCount = 0;
        var physicalCount = 0;
        var sourceOffset = 0;
        var destinationOffset = 0;
        while (logicalCount < descriptorCapacity)
        {
            var definitionOffset = page.ValueOffset + logicalCount;
            if (!definitionBits.IsEmpty &&
                ((definitionBits[definitionOffset >> 3] >> (definitionOffset & 7)) & 1) == 0)
            {
                logicalCount++;
                continue;
            }

            if (remainingPayload.Length - sourceOffset < sizeof(int))
                throw new CorruptParquetException(
                    "Payload too short to read byte array length prefix.");
            var unsignedLength = BinaryPrimitives.ReadUInt32LittleEndian(
                remainingPayload[sourceOffset..]);
            if (unsignedLength > int.MaxValue)
                throw new CorruptParquetException(
                    $"Byte array length {unsignedLength} exceeds the supported maximum of {int.MaxValue}.");
            var length = (int)unsignedLength;
            if (length > remainingPayload.Length - sourceOffset - sizeof(int))
                throw new CorruptParquetException(
                    $"Byte array length {length} exceeds remaining payload " +
                    $"({remainingPayload.Length - sourceOffset - sizeof(int)} bytes).");
            if (physicalCount != 0 && length > destinationPayload.Length - destinationOffset)
                break;

            sourceOffset += sizeof(int);
            remainingPayload.Slice(sourceOffset, length).CopyTo(
                destinationPayload[destinationOffset..]);
            destination[logicalCount] = new BinaryValueDescriptor(destinationOffset, length);
            sourceOffset += length;
            destinationOffset += length;
            physicalCount++;
            logicalCount++;
        }

        page.ValueOffset += logicalCount;
        page.PhysicalOffset += physicalCount;
        page.BinaryPayloadOffset += sourceOffset;
        if (!page.Active)
        {
            if (page.PhysicalOffset != page.PhysicalCount)
                throw new CorruptParquetException(
                    $"Definition levels consumed {page.PhysicalOffset} physical values, " +
                    $"expected {page.PhysicalCount}.");
            if (page.BinaryPayloadOffset != payload.Length)
                throw new CorruptParquetException(
                    $"PlainBinary payload contains " +
                    $"{payload.Length - page.BinaryPayloadOffset} trailing bytes.");
        }
        return buffers.CreateNativeBinaryBuffer(logicalCount, descriptorCapacity);
    }

    static int DecodeBorrowedPlainBinaryBatch<T>(ReadOnlySpan<byte> payload,
        ref BinaryDefinitionCursor definitions, int logicalCount, int physicalCount,
        ref ColumnReadBuffers<T> buffers, IParquetBufferPool bufferPool)
    {
        var destination = buffers.GetBinaryValues(logicalCount, 0, bufferPool, out _);
        ClearMissingBinaryDescriptors(destination, physicalCount);
        var sourceOffset = 0;
        for (var physicalIndex = 0; physicalIndex < physicalCount; physicalIndex++)
        {
            if (payload.Length - sourceOffset < sizeof(int))
                throw new CorruptParquetException(
                    "Payload too short to read byte array length prefix.");
            var unsignedLength = BinaryPrimitives.ReadUInt32LittleEndian(payload[sourceOffset..]);
            if (unsignedLength > int.MaxValue)
                throw new CorruptParquetException(
                    $"Byte array length {unsignedLength} exceeds the supported maximum of {int.MaxValue}.");
            var length = (int)unsignedLength;
            sourceOffset += sizeof(int);
            if (length > payload.Length - sourceOffset)
                throw new CorruptParquetException(
                    $"Byte array length {length} exceeds remaining payload ({payload.Length - sourceOffset} bytes).");
            var targetIndex = definitions.GetLogicalIndex(physicalIndex);
            destination[targetIndex] = new BinaryValueDescriptor(sourceOffset, length);
            sourceOffset += length;
        }
        return sourceOffset;
    }

    static int GetPlainBinaryBatchEncodedLength(ReadOnlySpan<byte> payload, Column column,
        int physicalCount)
    {
        if (column.PhysicalType != ParquetPhysicalType.ByteArray)
            return GetFixedBinaryPayloadLength(payload, physicalCount, GetFixedBinaryLength(column));

        var offset = 0;
        for (var i = 0; i < physicalCount; i++)
        {
            if (payload.Length - offset < sizeof(int))
                throw new CorruptParquetException(
                    "Payload too short to read byte array length prefix.");
            var length = BinaryPrimitives.ReadUInt32LittleEndian(payload[offset..]);
            if (length > int.MaxValue)
                throw new CorruptParquetException(
                    $"Byte array length {length} exceeds the supported maximum of {int.MaxValue}.");
            offset += sizeof(int);
            if (length > (uint)(payload.Length - offset))
                throw new CorruptParquetException(
                    $"Byte array length {length} exceeds remaining payload ({payload.Length - offset} bytes).");
            offset += checked((int)length);
        }
        return offset;
    }

    static void DecodeByteStreamSplitBinaryBatch<T>(ReadOnlySpan<byte> payload,
        ref BinaryDefinitionCursor definitions, int logicalCount, int physicalCount, Column column,
        ref ColumnReadBuffers<T> buffers, IParquetBufferPool bufferPool,
        ref EncodedPageState page)
    {
        var valueLength = GetFixedBinaryLength(column);
        var payloadByteLength = checked(physicalCount * valueLength);
        var destination = buffers.GetBinaryValues(logicalCount, payloadByteLength,
            bufferPool, out var destinationPayload);
        ClearMissingBinaryDescriptors(destination, physicalCount);
        var destinationOffset = 0;
        for (var physicalIndex = 0; physicalIndex < physicalCount; physicalIndex++)
        {
            var targetIndex = definitions.GetLogicalIndex(physicalIndex);
            var value = destinationPayload.Slice(destinationOffset, valueLength);
            for (var lane = 0; lane < valueLength; lane++)
                value[lane] = payload[checked(lane * page.PhysicalCount +
                    page.PhysicalOffset + physicalIndex)];
            destination[targetIndex] = new BinaryValueDescriptor(destinationOffset, valueLength);
            destinationOffset += valueLength;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    static void MaterializeBinaryDictionaryBatch<T>(ReadOnlySpan<int> indexes,
        ReadOnlySpan<byte> definitions, int valueOffset, int logicalCount,
        ref ColumnReadBuffers<T> buffers,
        IParquetBufferPool bufferPool, out ReadOnlyMemory<byte> borrowedValues)
    {
        borrowedValues = default;
        var dictionary = buffers.GetDictionary<BinaryValueDescriptor>();
        if (!buffers.BorrowedBinaryDictionaryPayload.IsEmpty)
        {
            var destination = buffers.GetBinaryValues(logicalCount, 0, bufferPool, out _);
            ClearMissingBinaryDescriptors(destination, indexes.Length);
            if (definitions.IsEmpty)
            {
                for (var i = 0; i < indexes.Length; i++)
                    destination[i] = dictionary[indexes[i]];
            }
            else
            {
                var physicalIndex = 0;
                for (var logicalIndex = 0; logicalIndex < logicalCount; logicalIndex++)
                {
                    var bitOffset = valueOffset + logicalIndex;
                    if (((definitions[bitOffset >> 3] >> (bitOffset & 7)) & 1) == 0)
                        continue;
                    if ((uint)physicalIndex >= (uint)indexes.Length)
                        throw new CorruptParquetException(
                            "Definition levels contain more non-null values than the encoded payload.");
                    destination[logicalIndex] = dictionary[indexes[physicalIndex++]];
                }
                if (physicalIndex != indexes.Length)
                    throw new CorruptParquetException(
                        "Definition levels contain fewer non-null values than the encoded payload.");
            }
            borrowedValues = buffers.BorrowedBinaryDictionaryPayload;
            return;
        }

        var payloadByteLength = 0;
        for (var i = 0; i < indexes.Length; i++)
            payloadByteLength = AddBinaryLength(payloadByteLength, dictionary[indexes[i]].Length);
        var values = buffers.GetBinaryValues(logicalCount, payloadByteLength,
            bufferPool, out var valuePayload);
        ClearMissingBinaryDescriptors(values, indexes.Length);
        var dictionaryAddress = GetBinaryPayloadAddress(buffers.Dictionary, dictionary.Length);
        var physicalValueIndex = 0;
        var destinationOffset = 0;
        for (var logicalIndex = 0; logicalIndex < logicalCount; logicalIndex++)
        {
            if (!definitions.IsEmpty)
            {
                var bitOffset = valueOffset + logicalIndex;
                if (((definitions[bitOffset >> 3] >> (bitOffset & 7)) & 1) == 0)
                    continue;
            }
            if ((uint)physicalValueIndex >= (uint)indexes.Length)
                throw new CorruptParquetException(
                    "Definition levels contain more non-null values than the encoded payload.");
            var dictionaryValue = dictionary[indexes[physicalValueIndex++]];
            dictionaryValue.GetSpan(dictionaryAddress).CopyTo(valuePayload[destinationOffset..]);
            values[logicalIndex] = new BinaryValueDescriptor(destinationOffset,
                dictionaryValue.Length);
            destinationOffset += dictionaryValue.Length;
        }
        if (physicalValueIndex != indexes.Length)
            throw new CorruptParquetException(
                "Definition levels contain fewer non-null values than the encoded payload.");
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    static void DecodeDeltaLengthBinaryBatch<T>(ReadOnlySpan<byte> payload,
        ReadOnlySpan<byte> definitions, int logicalCount, int physicalCount,
        ref ColumnReadBuffers<T> buffers, IParquetBufferPool bufferPool,
        ref EncodedPageState page, out ReadOnlyMemory<byte> borrowedValues)
    {
        borrowedValues = default;
        var lengths = MemoryMarshal.Cast<byte, int>(
            buffers.GetScratch(checked(physicalCount * sizeof(int)), bufferPool));
        DeltaBinaryPackedDecoder.ReadNonNegativeInt32Batch(payload, lengths, ref page.Delta);
        var remainingPayload = payload[(page.BinaryDataOffset + page.BinaryPayloadOffset)..];
        var payloadByteLength = SumBinaryLengths(lengths, remainingPayload.Length,
            "Delta length byte array");
        var source = remainingPayload[..payloadByteLength];

        if (!page.BorrowedDataPayload.IsEmpty && payloadByteLength != 0)
        {
            var destination = buffers.GetBinaryValues(logicalCount, 0, bufferPool, out _);
            ClearMissingBinaryDescriptors(destination, physicalCount);
            var sourceOffset = 0;
            if (definitions.IsEmpty)
            {
                for (var i = 0; i < physicalCount; i++)
                {
                    destination[i] = new BinaryValueDescriptor(sourceOffset, lengths[i]);
                    sourceOffset += lengths[i];
                }
            }
            else
                FillDeltaLengthDescriptors(definitions, page.ValueOffset, lengths,
                    destination, ref sourceOffset);
            borrowedValues = page.BorrowedDataPayload.Slice(
                page.BinaryDataOffset + page.BinaryPayloadOffset, payloadByteLength);
        }
        else
        {
            var destination = buffers.GetBinaryValues(logicalCount, payloadByteLength,
                bufferPool, out var destinationPayload);
            ClearMissingBinaryDescriptors(destination, physicalCount);
            source.CopyTo(destinationPayload);
            var sourceOffset = 0;
            if (definitions.IsEmpty)
            {
                for (var i = 0; i < physicalCount; i++)
                {
                    destination[i] = new BinaryValueDescriptor(sourceOffset, lengths[i]);
                    sourceOffset += lengths[i];
                }
            }
            else
                FillDeltaLengthDescriptors(definitions, page.ValueOffset, lengths,
                    destination, ref sourceOffset);
        }
        page.BinaryPayloadOffset += payloadByteLength;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    static void FillDeltaLengthDescriptors(ReadOnlySpan<byte> definitions, int valueOffset,
        ReadOnlySpan<int> lengths, Span<BinaryValueDescriptor> destination,
        ref int sourceOffset)
    {
        var physicalIndex = 0;
        for (var logicalIndex = 0; logicalIndex < destination.Length; logicalIndex++)
        {
            var bitOffset = valueOffset + logicalIndex;
            if (((definitions[bitOffset >> 3] >> (bitOffset & 7)) & 1) == 0)
                continue;
            if ((uint)physicalIndex >= (uint)lengths.Length)
                throw new CorruptParquetException(
                    "Definition levels contain more non-null values than the encoded payload.");
            var length = lengths[physicalIndex++];
            destination[logicalIndex] = new BinaryValueDescriptor(sourceOffset, length);
            sourceOffset += length;
        }
        if (physicalIndex != lengths.Length)
            throw new CorruptParquetException(
                "Definition levels contain fewer non-null values than the encoded payload.");
    }

    static void DecodeDeltaByteArrayBinaryBatch<T>(ReadOnlySpan<byte> payload,
        ref BinaryDefinitionCursor definitions, int logicalCount, int physicalCount, Column column,
        ref ColumnReadBuffers<T> buffers, IParquetBufferPool bufferPool,
        ref EncodedPageState page)
    {
        var scratch = MemoryMarshal.Cast<byte, int>(
            buffers.GetScratch(checked(physicalCount * 2 * sizeof(int)), bufferPool));
        var prefixes = scratch[..physicalCount];
        var suffixes = scratch[physicalCount..];
        var prefixByteLength = DeltaBinaryPackedDecoder.ReadNonNegativeInt32BatchWithTotal(
            payload, prefixes, ref page.Delta);
        var suffixByteLength = DeltaBinaryPackedDecoder.ReadNonNegativeInt32BatchWithTotal(
            payload[page.BinaryAuxOffset..],
            suffixes, ref page.Delta2);

        var remainingSuffixBytes = payload.Length - page.BinaryDataOffset - page.BinaryPayloadOffset;
        if (suffixByteLength > remainingSuffixBytes)
            throw new CorruptParquetException(
                $"Delta byte array suffix length {suffixByteLength} exceeds remaining suffix bytes ({remainingSuffixBytes}).");
        var totalByteLength = prefixByteLength + suffixByteLength;
        if (totalByteLength > int.MaxValue)
            throw new CorruptParquetException(
                $"Binary payload length exceeds the supported maximum of {int.MaxValue} bytes.");
        var payloadByteLength = (int)totalByteLength;

        var destination = buffers.GetBinaryValues(logicalCount, payloadByteLength,
            bufferPool, out var destinationPayload);
        ClearMissingBinaryDescriptors(destination, physicalCount);
        var suffixPayload = payload[(page.BinaryDataOffset + page.BinaryPayloadOffset)..];
        var previous = buffers.GetPreviousBinaryValue(page.PreviousBinaryLength);
        var destinationOffset = 0;
        var previousOffset = 0;
        var previousLength = page.PreviousBinaryLength;
        var suffixOffset = 0;
        for (var i = 0; i < physicalCount; i++)
        {
            var prefixLength = prefixes[i];
            var suffixLength = suffixes[i];
            var length = prefixLength + suffixLength;
            if (prefixLength > previousLength)
                throw new CorruptParquetException(
                    $"Delta byte array prefix length {prefixLength} exceeds previous value length {previousLength}.");
            if (page.BinaryFixedLength >= 0 && length != page.BinaryFixedLength)
                throw new CorruptParquetException(
                    $"Delta byte array value reconstructs to {length} bytes but column " +
                    $"'{column.Name}' is fixed at {page.BinaryFixedLength}.");
            var targetIndex = definitions.GetLogicalIndex(i);
            var value = destinationPayload.Slice(destinationOffset, length);
            if (prefixLength != 0)
            {
                var prefix = i == 0
                    ? previous[..prefixLength]
                    : destinationPayload.Slice(previousOffset, prefixLength);
                Plank.Writing.Encoding.EncodingPrimitives.CopyPayload(prefix, value);
            }
            Plank.Writing.Encoding.EncodingPrimitives.CopyPayload(
                suffixPayload.Slice(suffixOffset, suffixLength), value[prefixLength..]);
            destination[targetIndex] = new BinaryValueDescriptor(destinationOffset, length);
            previousOffset = destinationOffset;
            previousLength = length;
            destinationOffset += length;
            suffixOffset += suffixLength;
        }

        page.PreviousBinaryLength = previousLength;
        destinationPayload.Slice(previousOffset, previousLength).CopyTo(
            buffers.GetPreviousBinaryValue(previousLength, bufferPool));
        page.BinaryPayloadOffset += suffixOffset;
    }

    static void DecodeFixedDeltaByteArrayBatch<TPage, TValue>(ReadOnlySpan<byte> payload,
        Column column, Span<TValue> destination, ref EncodedPageState page,
        ref ColumnReadBuffers<TPage> buffers, IParquetBufferPool bufferPool)
        where TValue : struct
    {
        if (typeof(TValue) != typeof(Guid) && typeof(TValue) != typeof(decimal))
            throw new InvalidOperationException(
                $"Delta byte array decoding declined '{typeof(TValue)}'.");
        var lengthCount = checked(destination.Length * 2);
        using var temporaryLengths = column.Converter is not null && page.IsNullable
            ? bufferPool.Rent(checked((uint)(lengthCount * sizeof(int))))
            : default;
        var lengths = temporaryLengths.IsEmpty
            ? buffers.GetExpandedDefinitions(lengthCount, bufferPool)
            : ParquetBuffer.AsSpan<int>(temporaryLengths, lengthCount);
        var prefixes = lengths[..destination.Length];
        var suffixes = lengths[destination.Length..];
        DeltaBinaryPackedDecoder.ReadNonNegativeInt32Batch(payload, prefixes, ref page.Delta);
        DeltaBinaryPackedDecoder.ReadNonNegativeInt32Batch(payload[page.BinaryAuxOffset..],
            suffixes, ref page.Delta2);

        var valueLength = GetFixedBinaryLength(column);
        if (valueLength > 256)
            throw new CorruptParquetException(
                $"Fixed delta byte array length {valueLength} exceeds the supported maximum of 256 bytes.");
        Span<byte> value = stackalloc byte[valueLength];
        var previous = buffers.GetPreviousBinaryValue(page.PreviousBinaryLength);
        if (!previous.IsEmpty)
            previous.CopyTo(value);
        var suffixPayload = payload[(page.BinaryDataOffset + page.BinaryPayloadOffset)..];
        var suffixOffset = 0;
        for (var i = 0; i < destination.Length; i++)
        {
            var prefixLength = prefixes[i];
            var suffixLength = suffixes[i];
            if (prefixLength > valueLength || suffixLength > valueLength ||
                prefixLength + suffixLength != valueLength)
                throw new CorruptParquetException(
                    $"Delta byte array value reconstructs to {prefixLength + suffixLength} bytes " +
                    $"but column '{column.Name}' is fixed at {valueLength}.");
            if (suffixLength > suffixPayload.Length - suffixOffset)
                throw new CorruptParquetException(
                    $"Delta byte array suffix length {suffixLength} exceeds remaining suffix bytes " +
                    $"({suffixPayload.Length - suffixOffset}).");
            suffixPayload.Slice(suffixOffset, suffixLength).CopyTo(value[prefixLength..]);
            suffixOffset += suffixLength;
            if (typeof(TValue) == typeof(decimal))
                Unsafe.As<Span<TValue>, Span<decimal>>(ref destination)[i] =
                    ParquetDecimalConverter.ReadBigEndian(value, column);
            else
                Unsafe.As<Span<TValue>, Span<Guid>>(ref destination)[i] =
                    new Guid(value, bigEndian: true);
        }
        value.CopyTo(buffers.GetPreviousBinaryValue(valueLength, bufferPool));
        page.PreviousBinaryLength = valueLength;
        page.BinaryPayloadOffset += suffixOffset;
        if (!page.Delta.Active && page.BinaryDataOffset + page.BinaryPayloadOffset != payload.Length)
            throw new CorruptParquetException(
                $"DeltaByteArray payload contains " +
                $"{payload.Length - page.BinaryDataOffset - page.BinaryPayloadOffset} trailing bytes.");
    }
}
