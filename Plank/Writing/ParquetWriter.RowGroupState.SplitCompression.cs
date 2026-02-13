using System.Buffers;

namespace Plank.Writing;

public sealed partial class ParquetWriter
{
    internal sealed partial class RowGroupState
    {        void WriteSplitLevelFixedWidthPages(ParquetWriter writer, int ordinal, ref ColumnState state, int bytesPerValue, out long totalUncompressedSize, out long totalCompressedSize)
        {
            totalUncompressedSize = 0;
            totalCompressedSize = 0;

            var isRepeated = state.RepetitionLevelsByteLength > 0;
            var maxDefLevel = GetMaxDefinitionLevel(writer, ordinal, isRepeated);
            var levelCount = state.ValueCount;
            if (levelCount <= 0)
                return;

            var repLevels = isRepeated ? new byte[levelCount] : [];
            if (isRepeated)
            {
                var repSource = GetSourceSpan(ref state, 0, state.RepetitionLevelsByteLength);
                DecodeBitPackedLevels(repSource, levelCount, 1, repLevels);
            }

            var defLevels = maxDefLevel > 0 ? new byte[levelCount] : [];
            if (maxDefLevel > 0)
            {
                var defSource = GetSourceSpan(ref state, state.RepetitionLevelsByteLength, state.DefinitionLevelsByteLength);
                var defBitWidth = maxDefLevel == 1 ? 1 : 2;
                DecodeBitPackedLevels(defSource, levelCount, defBitWidth, defLevels);
            }

            var dataSourceOffset = state.RepetitionLevelsByteLength + state.DefinitionLevelsByteLength;
            var source = GetSourceSpan(ref state, dataSourceOffset, state.EncodedLength - dataSourceOffset);

            var maxValues = _options.RowGroupOptions.MaxPageValueCount > 0 ? _options.RowGroupOptions.MaxPageValueCount : int.MaxValue;
            var maxBytes = _options.RowGroupOptions.MaxPageBytes > 0 ? _options.RowGroupOptions.MaxPageBytes : int.MaxValue;
            var levelIndex = 0;
            var definedBefore = 0;
            while (levelIndex < levelCount)
            {
                var pageStart = levelIndex;
                var pageLevels = 0;
                var pageRows = 0;
                var pageNulls = 0;
                var pageDefined = 0;
                var overflow = false;
                while (levelIndex < levelCount)
                {
                    if (!isRepeated || repLevels[levelIndex] == 0)
                        pageRows++;

                    var def = maxDefLevel == 0 ? (byte)0 : defLevels[levelIndex];
                    if (maxDefLevel > 0 && def < maxDefLevel)
                        pageNulls++;
                    else
                        pageDefined++;

                    pageLevels++;
                    levelIndex++;

                    if (overflow)
                    {
                        if (!isRepeated || levelIndex >= levelCount || repLevels[levelIndex] == 0)
                            break;
                        continue;
                    }

                    var estBytes = checked((pageDefined * bytesPerValue) + GetLevelEncodedSize(pageLevels, isRepeated ? 1 : 0) + GetLevelEncodedSize(pageLevels, maxDefLevel == 0 ? 0 : (maxDefLevel == 1 ? 1 : 2)));
                    if (pageLevels >= maxValues || estBytes >= maxBytes)
                    {
                        if (!isRepeated)
                            break;
                        overflow = true;
                    }
                }

                var pageRepLen = isRepeated ? GetLevelEncodedSize(pageLevels, 1) : 0;
                var defBitWidth = maxDefLevel == 0 ? 0 : (maxDefLevel == 1 ? 1 : 2);
                var pageDefLen = GetLevelEncodedSize(pageLevels, defBitWidth);
                var pageDataLen = checked(pageDefined * bytesPerValue);
                var pageLen = checked(pageRepLen + pageDefLen + pageDataLen);
                var pageBuffer = ArrayPool<byte>.Shared.Rent(pageLen);
                try
                {
                    var pageSpan = pageBuffer.AsSpan(0, pageLen);
                    var offset = 0;
                    if (isRepeated)
                        offset += WriteBitPackedLevels(repLevels.AsSpan(pageStart, pageLevels), 1, pageSpan[offset..]);
                    if (defBitWidth > 0)
                        offset += WriteBitPackedLevels(defLevels.AsSpan(pageStart, pageLevels), defBitWidth, pageSpan[offset..]);

                    var definedIndex = definedBefore;
                    for (var i = 0; i < pageLevels; i++)
                    {
                        var level = maxDefLevel == 0 ? (byte)0 : defLevels[pageStart + i];
                        if (maxDefLevel > 0 && level < maxDefLevel)
                            continue;

                        var srcOffset = checked(definedIndex * bytesPerValue);
                        source.Slice(srcOffset, bytesPerValue).CopyTo(pageSpan[offset..]);
                        offset += bytesPerValue;
                        definedIndex++;
                    }

                    WritePageFromPayload(
                        writer,
                        ordinal,
                        ref state,
                        pageSpan,
                        pageLevels,
                        pageRows,
                        pageNulls,
                        pageDefLen,
                        pageRepLen,
                        ref totalUncompressedSize,
                        ref totalCompressedSize);
                    definedBefore = definedIndex;
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(pageBuffer);
                }
            }
        }

        void WritePageFromPayload(ParquetWriter writer, int ordinal, ref ColumnState state, ReadOnlySpan<byte> payload,
            int valueCount, int rowCount, int nullCount, int definitionLevelsByteLength, int repetitionLevelsByteLength,
            ref long totalUncompressedSize, ref long totalCompressedSize)
        {
            var levelsByteLength = checked(definitionLevelsByteLength + repetitionLevelsByteLength);
            if (levelsByteLength > payload.Length)
                throw new InvalidOperationException("Data page level section exceeds payload length.");

            var levelsPayload = payload[..levelsByteLength];
            var dataPayload = payload[levelsByteLength..];
            var compressedDataPayload = dataPayload;
            var compressedDataLength = dataPayload.Length;
            var isCompressed = false;
            if (state.Compression != CompressionKind.None && dataPayload.Length > 0)
            {
                var compressor = writer._pageCompressors.Select(state.Compression);
                compressedDataLength = CompressPayload(writer, compressor, dataPayload, ordinal, ref state, _options.RowGroupOptions.MaxCompressedBytes);
                compressedDataPayload = compressor.UsesStreamingOutput
                    ? writer._streamingCompressedBuffer.WrittenSpan
                    : state.CompressedBufferOwner!.Memory.Span[..compressedDataLength];
                isCompressed = true;
            }

            var uncompressedPayloadLength = checked(levelsByteLength + dataPayload.Length);
            var compressedPayloadLength = checked(levelsByteLength + compressedDataLength);

            var headerLength = ParquetThriftWriter.WriteDataPageHeader(
                _pageHeaderBuffer,
                valueCount,
                nullCount,
                rowCount,
                state.Encoding,
                definitionLevelsByteLength,
                repetitionLevelsByteLength,
                uncompressedSize: uncompressedPayloadLength,
                compressedSize: compressedPayloadLength,
                isCompressed);
            writer.WriteToStream(_pageHeaderBuffer.AsSpan(0, headerLength));
            writer.AdvancePosition(headerLength);
            totalUncompressedSize = checked(totalUncompressedSize + headerLength + uncompressedPayloadLength);
            totalCompressedSize = checked(totalCompressedSize + headerLength + compressedPayloadLength);
            if (compressedPayloadLength == 0)
                return;
            if (levelsPayload.Length > 0)
                writer.WriteToStream(levelsPayload);
            if (compressedDataLength > 0)
                writer.WriteToStream(compressedDataPayload);
            writer.AdvancePosition(compressedPayloadLength);
        }

        void WritePage(ParquetWriter writer, int ordinal, ref ColumnState state, int valueCount, int rowCount, int nullCount, int definitionLevelsByteLength, int repetitionLevelsByteLength, int encodedOffset, int encodedLength, ref long totalUncompressedSize, ref long totalCompressedSize)
        {
            var payload = GetSourceSpan(ref state, encodedOffset, encodedLength);
            WritePageFromPayload(writer, ordinal, ref state, payload, valueCount, rowCount, nullCount,
                definitionLevelsByteLength, repetitionLevelsByteLength, ref totalUncompressedSize, ref totalCompressedSize);
        }

        static ReadOnlySpan<byte> GetSourceSpan(ref ColumnState state, int offset, int length)
        {
            if (!state.ExternalData.IsEmpty)
                return state.ExternalData.Span.Slice(offset, length);
            if (state.EncodedBuffer is not null)
                return state.EncodedBuffer.AsSpan(offset, length);
            if (state.EncodedBufferOwner is not null)
                return state.EncodedBufferOwner.Memory.Span.Slice(offset, length);
            throw new InvalidOperationException("Serialized payload is missing.");
        }

        int CompressPayload(ParquetWriter writer, IPageCompressor compressor, ReadOnlySpan<byte> source, int ordinal, ref ColumnState state, int maxCompressedBytes)
        {
            if (compressor.UsesStreamingOutput)
            {
                writer._streamingCompressedBuffer.Reset(maxCompressedBytes);
                return compressor.Compress(source, writer._streamingCompressedBuffer);
            }

            if (state.CompressedBufferOwner is null)
                state.CompressedBufferOwner = _buffers.RentCompressed(ordinal);

            var destination = state.CompressedBufferOwner.Memory.Span;
            if (maxCompressedBytes > 0 && maxCompressedBytes < destination.Length)
                destination = destination[..maxCompressedBytes];
            if (destination.Length == 0)
                throw new InvalidOperationException("Compressed buffer is too small.");

            return compressor.Compress(source, destination);
        }

        static int GetLevelEncodedSize(int valueCount, int bitWidth)
        {
            if (bitWidth == 0 || valueCount == 0)
                return 0;
            var groupCount = (valueCount + 7) >> 3;
            var header = (uint)((groupCount << 1) | 1);
            return GetVarUInt32Length(header) + (groupCount * bitWidth);
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

        static void DecodeBitPackedLevels(ReadOnlySpan<byte> source, int valueCount, int bitWidth, Span<byte> destination)
        {
            if (valueCount == 0)
                return;

            var offset = 0;
            _ = ReadVarUInt32(source, ref offset);
            var groups = (valueCount + 7) >> 3;
            if (bitWidth == 1)
            {
                for (var group = 0; group < groups; group++)
                {
                    var packed = source[offset++];
                    for (var i = 0; i < 8; i++)
                    {
                        var index = (group << 3) + i;
                        if (index >= valueCount)
                            break;
                        destination[index] = (byte)((packed >> i) & 1);
                    }
                }
                return;
            }

            for (var group = 0; group < groups; group++)
            {
                var packed = (ushort)(source[offset] | (source[offset + 1] << 8));
                offset += 2;
                for (var i = 0; i < 8; i++)
                {
                    var index = (group << 3) + i;
                    if (index >= valueCount)
                        break;
                    destination[index] = (byte)((packed >> (i * 2)) & 0x3);
                }
            }
        }

        static int WriteBitPackedLevels(ReadOnlySpan<byte> levels, int bitWidth, Span<byte> destination)
        {
            if (levels.Length == 0 || bitWidth == 0)
                return 0;

            var groups = (levels.Length + 7) >> 3;
            var header = (uint)((groups << 1) | 1);
            var offset = WriteVarUInt32(header, destination);
            if (bitWidth == 1)
            {
                for (var group = 0; group < groups; group++)
                {
                    byte packed = 0;
                    for (var i = 0; i < 8; i++)
                    {
                        var index = (group << 3) + i;
                        if (index >= levels.Length)
                            break;
                        packed |= (byte)((levels[index] & 1) << i);
                    }
                    destination[offset++] = packed;
                }
                return offset;
            }

            for (var group = 0; group < groups; group++)
            {
                ushort packed = 0;
                for (var i = 0; i < 8; i++)
                {
                    var index = (group << 3) + i;
                    if (index >= levels.Length)
                        break;
                    packed |= (ushort)((levels[index] & 0x3) << (i * 2));
                }
                destination[offset++] = (byte)packed;
                destination[offset++] = (byte)(packed >> 8);
            }
            return offset;
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

        static uint ReadVarUInt32(ReadOnlySpan<byte> source, ref int offset)
        {
            uint value = 0;
            var shift = 0;
            while (true)
            {
                var current = source[offset++];
                value |= (uint)(current & 0x7F) << shift;
                if ((current & 0x80) == 0)
                    return value;
                shift += 7;
            }
        }

        static int GetMaxDefinitionLevel(ParquetWriter writer, int ordinal, bool isRepeated)
        {
            if (!isRepeated)
                return 1;
            return writer._semanticRegistry.IsRepeatedElementOptional(ordinal) ? 2 : 1;
        }

    }
}
