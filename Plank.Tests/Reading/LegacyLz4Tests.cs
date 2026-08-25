using System.Buffers.Binary;
using System.IO.Hashing;
using K4os.Compression.LZ4;
using K4os.Compression.LZ4.Streams;
using K4os.Hash.xxHash;
using Plank.Reading;
using Plank.Reading.Logical;
using Plank.Reading.Physical;
using Plank.Schema;

namespace Plank.Tests.Reading;

[NotInParallel]
internal sealed class LegacyLz4Tests
{
    const int BlockSize = 64 * 1024;

    [Test]
    public async Task DecodesStandardFrameWithLinkedBlocksAndChecksums()
    {
        var input = CreateLinkedBlockInput();
        var frame = CreateFrame(input, chainBlocks: true);

        var decoded = ParquetDecompressor.Decompress(frame, (uint)input.Length, CompressionKind.Lz4Legacy);

        await Assert.That(decoded).IsEquivalentTo(input);
    }

    [Test]
    public async Task DecodesStandardFrameWithDeclaredContentSize()
    {
        var input = CreateLinkedBlockInput();
        var frame = AddContentSize(CreateFrame(input, chainBlocks: true), (ulong)input.Length);

        var decoded = ParquetDecompressor.Decompress(frame, (uint)input.Length, CompressionKind.Lz4Legacy);

        await Assert.That(decoded).IsEquivalentTo(input);
    }

    [Test]
    public async Task DecodesConcatenatedAndSkippableStandardFrames()
    {
        byte[] first = [1, 2, 3, 4, 5];
        byte[] second = [10, 20, 30, 40, 50, 60];
        var firstFrame = CreateFrame(first, chainBlocks: false);
        var secondFrame = CreateFrame(second, chainBlocks: false);
        var sequence = AddSkippablePrefix(firstFrame, secondFrame);
        var expected = first.Concat(second).ToArray();

        var decoded = ParquetDecompressor.Decompress(sequence, (uint)expected.Length,
            CompressionKind.Lz4Legacy);

        await Assert.That(decoded).IsEquivalentTo(expected);
    }

    [Test]
    public void RejectsInvalidStandardFrameHeaderChecksum()
    {
        var input = CreateLinkedBlockInput();
        var frame = CreateFrame(input, chainBlocks: true);
        frame[GetHeaderChecksumOffset(frame)] ^= 0x80;

        AssertCorrupt(frame, input.Length);
    }

    [Test]
    public void RejectsInvalidStandardFrameBlockChecksum()
    {
        var input = CreateLinkedBlockInput();
        var frame = CreateFrame(input, chainBlocks: true);
        frame[GetFirstBlockChecksumOffset(frame)] ^= 0x80;

        AssertCorrupt(frame, input.Length);
    }

    [Test]
    public void RejectsInvalidStandardFrameContentChecksum()
    {
        var input = CreateLinkedBlockInput();
        var frame = CreateFrame(input, chainBlocks: true);
        frame[^1] ^= 0x80;

        AssertCorrupt(frame, input.Length);
    }

    [Test]
    public void RejectsTruncatedStandardFrame()
    {
        var input = CreateLinkedBlockInput();
        var frame = CreateFrame(input, chainBlocks: true);

        AssertCorrupt(frame.AsSpan(0, frame.Length - 1), input.Length);
    }

    [Test]
    public void RejectsMismatchedStandardFrameContentSize()
    {
        var input = CreateLinkedBlockInput();
        var frame = AddContentSize(CreateFrame(input, chainBlocks: true), (ulong)input.Length + 1);

        AssertCorrupt(frame, input.Length);
    }

    [Test]
    public async Task DecodesMultipleHadoopBlocks()
    {
        var input = CreateLinkedBlockInput();
        var hadoopPayload = CreateHadoopPayload(input);

        var decoded = ParquetDecompressor.Decompress(hadoopPayload, (uint)input.Length,
            CompressionKind.Lz4Legacy);

        await Assert.That(decoded).IsEquivalentTo(input);
    }

    [Test]
    public void RejectsMalformedHadoopBlockAfterValidFirstBlock()
    {
        var input = CreateLinkedBlockInput();
        var hadoopPayload = CreateHadoopPayload(input);

        AssertCorrupt(hadoopPayload.AsSpan(0, hadoopPayload.Length - 1), input.Length);
    }

    [Test]
    public async Task FallsBackToRawBlockFromOlderWriters()
    {
        var input = CreateLinkedBlockInput();
        var raw = new byte[LZ4Codec.MaximumOutputSize(input.Length)];
        var rawLength = LZ4Codec.Encode(input, raw, LZ4Level.L00_FAST);

        var legacyDecoded = ParquetDecompressor.Decompress(raw.AsSpan(0, rawLength), (uint)input.Length,
            CompressionKind.Lz4Legacy);
        var rawDecoded = ParquetDecompressor.Decompress(raw.AsSpan(0, rawLength), (uint)input.Length,
            CompressionKind.Lz4);

        await Assert.That(legacyDecoded).IsEquivalentTo(input);
        await Assert.That(rawDecoded).IsEquivalentTo(input);
    }

    [Test]
    public void RawCodecDoesNotAcceptFramedPayloads()
    {
        var input = CreateLinkedBlockInput();
        var frame = CreateFrame(input, chainBlocks: false);

        Assert.Throws<CorruptParquetException>(() =>
            ParquetDecompressor.Decompress(frame, (uint)input.Length, CompressionKind.Lz4));
    }

    [Test]
    public async Task ReadsParquetSharpHadoopLz4File()
        => await AssertReadsParquetSharpLegacyFile(ParquetSharp.Compression.Lz4Hadoop).ConfigureAwait(false);

    [Test]
    public void LegacyDecompressionDoesNotAllocateAfterWarmup()
    {
        var input = CreateLinkedBlockInput();
        var frame = CreateFrame(input, chainBlocks: true);
        var output = new byte[input.Length];
        for (var i = 0; i < 16; i++)
            ParquetDecompressor.DecompressInto(frame, CompressionKind.Lz4Legacy, output);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++)
            ParquetDecompressor.DecompressInto(frame, CompressionKind.Lz4Legacy, output);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        if (allocated != 0)
            throw new InvalidOperationException(
                $"Expected zero allocations for legacy LZ4 decompression but saw {allocated} bytes.");
    }

    [Test]
    public void XxHash32MatchesPublishedVectors()
    {
        if (XxHash32.HashToUInt32([]) != 0x02CC5D05)
            throw new InvalidOperationException("The empty XXH32 vector did not match.");
        if (XxHash32.HashToUInt32("abc"u8) != 0x32D153FF)
            throw new InvalidOperationException("The 'abc' XXH32 vector did not match.");
        var input = CreateLinkedBlockInput();
        if (XxHash32.HashToUInt32(input) != XXH32.DigestOf(input))
            throw new InvalidOperationException("XXH32 did not match the independent implementation.");
    }

    static async Task AssertReadsParquetSharpLegacyFile(ParquetSharp.Compression compression)
    {
        int[] expected = Enumerable.Range(0, 20_000).Select(index => index % 997).ToArray();
        var file = CreateParquetSharpFile(expected, compression);
        using (var stream = new MemoryStream(file, writable: false))
        using (var physicalReader = new ParquetFileReader())
        {
            physicalReader.Reset(stream);
            await Assert.That(physicalReader.Metadata.ColumnChunk(0, 0).Compression)
                .IsEqualTo(CompressionKind.Lz4Legacy);
        }

        var schema = new ParquetSchema([
            ColumnDefinition.RequiredLeaf("Value", ParquetPhysicalType.Int32)
        ]);
        using var logicalStream = new MemoryStream(file, writable: false);
        using var logicalReader = schema.CreateReader(logicalStream);

        await Assert.That(ReadAll(logicalReader.RowGroups[0].Column<int>(0))).IsEquivalentTo(expected);
    }

    static byte[] CreateParquetSharpFile(int[] values, ParquetSharp.Compression compression)
    {
        using var output = new MemoryStream();
        using var propertiesBuilder = new ParquetSharp.WriterPropertiesBuilder();
        using var properties = propertiesBuilder.Compression(compression)
            .DisableDictionary()
            .Build();
        using (var writer = new ParquetSharp.ParquetFileWriter(output,
                   [new ParquetSharp.Column<int>("Value")], null, properties, null, true))
        {
            using var rowGroup = writer.AppendRowGroup();
            using var column = rowGroup.NextColumn().LogicalWriter<int>();
            column.WriteBatch(values);
        }
        return output.ToArray();
    }

    static byte[] CreateFrame(byte[] input, bool chainBlocks)
    {
        var target = new byte[LZ4Codec.MaximumOutputSize(input.Length) + 1024];
        var settings = new LZ4EncoderSettings
        {
            BlockSize = BlockSize,
            ChainBlocks = chainBlocks,
            BlockChecksum = true,
            ContentChecksum = true,
            CompressionLevel = LZ4Level.L00_FAST
        };
        var written = LZ4Frame.Encode(input.AsSpan(), target.AsSpan(), settings);
        return target.AsSpan(0, written).ToArray();
    }

    static byte[] CreateHadoopPayload(byte[] input)
    {
        var blockCount = (input.Length + BlockSize - 1) / BlockSize;
        var target = new byte[input.Length + blockCount * (sizeof(uint) * 2 + 512)];
        var sourceOffset = 0;
        var targetOffset = 0;
        while (sourceOffset < input.Length)
        {
            var length = Math.Min(BlockSize, input.Length - sourceOffset);
            var maximumLength = LZ4Codec.MaximumOutputSize(length);
            BinaryPrimitives.WriteUInt32BigEndian(target.AsSpan(targetOffset), (uint)length);
            var compressedOffset = targetOffset + sizeof(uint) * 2;
            var compressedLength = LZ4Codec.Encode(input.AsSpan(sourceOffset, length),
                target.AsSpan(compressedOffset, maximumLength), LZ4Level.L00_FAST);
            if (compressedLength <= 0)
                throw new InvalidOperationException("The Hadoop LZ4 test block could not be compressed.");
            BinaryPrimitives.WriteUInt32BigEndian(target.AsSpan(targetOffset + sizeof(uint)),
                (uint)compressedLength);
            sourceOffset += length;
            targetOffset = compressedOffset + compressedLength;
        }
        return target[..targetOffset];
    }

    static byte[] CreateLinkedBlockInput()
    {
        var input = new byte[BlockSize * 2 + 257];
        uint state = 1771;
        for (var i = 0; i < BlockSize; i++)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            input[i] = (byte)state;
        }
        input.AsSpan(0, BlockSize).CopyTo(input.AsSpan(BlockSize));
        input.AsSpan(0, 257).CopyTo(input.AsSpan(BlockSize * 2));
        return input;
    }

    static byte[] AddSkippablePrefix(byte[] firstFrame, byte[] secondFrame)
    {
        byte[] skipped = [0x10, 0x20, 0x30];
        var result = new byte[sizeof(uint) * 2 + skipped.Length + firstFrame.Length + secondFrame.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(result, 0x184D2A50);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(sizeof(uint)), (uint)skipped.Length);
        skipped.CopyTo(result.AsSpan(sizeof(uint) * 2));
        var offset = sizeof(uint) * 2 + skipped.Length;
        firstFrame.CopyTo(result.AsSpan(offset));
        secondFrame.CopyTo(result.AsSpan(offset + firstFrame.Length));
        return result;
    }

    static byte[] AddContentSize(byte[] frame, ulong contentSize)
    {
        var result = new byte[frame.Length + sizeof(ulong)];
        frame.AsSpan(0, sizeof(uint) + 2).CopyTo(result);
        result[sizeof(uint)] |= 0x08;
        BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(sizeof(uint) + 2), contentSize);
        var checksumOffset = sizeof(uint) + 2 + sizeof(ulong);
        result[checksumOffset] = (byte)(XXH32.DigestOf(result.AsSpan(sizeof(uint), 2 + sizeof(ulong))) >> 8);
        frame.AsSpan(sizeof(uint) + 3).CopyTo(result.AsSpan(checksumOffset + 1));
        return result;
    }

    static int GetHeaderChecksumOffset(byte[] frame)
    {
        var flags = frame[sizeof(uint)];
        return sizeof(uint) + 2 + ((flags & 0x08) == 0 ? 0 : sizeof(ulong)) +
            ((flags & 0x01) == 0 ? 0 : sizeof(uint));
    }

    static int GetFirstBlockChecksumOffset(byte[] frame)
    {
        var blockHeaderOffset = GetHeaderChecksumOffset(frame) + 1;
        var blockHeader = BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(blockHeaderOffset));
        return blockHeaderOffset + sizeof(uint) + (int)(blockHeader & 0x7FFFFFFF);
    }

    static int[] ReadAll(RowGroupColumn<int> column)
    {
        var values = new List<int>();
        foreach (var buffer in column)
            values.AddRange(buffer.Values);
        return values.ToArray();
    }

    static void AssertCorrupt(ReadOnlySpan<byte> payload, int expectedLength)
    {
        var copy = payload.ToArray();
        Assert.Throws<CorruptParquetException>(() =>
            ParquetDecompressor.Decompress(copy, (uint)expectedLength, CompressionKind.Lz4Legacy));
    }
}
