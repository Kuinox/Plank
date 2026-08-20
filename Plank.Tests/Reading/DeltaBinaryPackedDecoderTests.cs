using Plank.Reading;
using Plank.Reading.Logical;
using Plank.Reading.Logical.Internal;
using Plank.Writing;
using Plank.Writing.Encoding;

namespace Plank.Tests.Reading;

internal sealed class DeltaBinaryPackedDecoderTests
{
    [Test]
    public void ReadInt32HandlesDeltasWiderThanInt32()
    {
        var values = new[] { int.MinValue, int.MaxValue, int.MinValue, 0 };
        var writer = new BufferWriter(DefaultParquetBufferPool.Shared, 1024, 1024);
        try
        {
            DeltaBinaryPackedEncoding.WriteInt32(values, ref writer);

            var payload = new byte[writer.WrittenLength];
            writer.CopyTo(payload);

            var decoded = DeltaBinaryPackedDecoder.ReadInt32(payload);

            if (!decoded.SequenceEqual(values))
                throw new InvalidOperationException("Delta binary packed Int32 values did not round-trip.");
        }
        finally
        {
            writer.Dispose();
        }
    }

    [Test]
    public void ReadNullableInt32WritesDirectlyAcrossVectorWidthsAndScalarFallback()
    {
        var values = new int[257];
        values[0] = int.MinValue;
        values[1] = int.MaxValue;
        for (var i = 2; i < values.Length; i++)
            values[i] = i % 37 == 0
                ? unchecked(values[i - 1] + int.MaxValue)
                : unchecked(values[i - 1] + (i % 17) - 8);

        var writer = new BufferWriter(DefaultParquetBufferPool.Shared, 1024, 1024);
        try
        {
            DeltaBinaryPackedEncoding.WriteInt32(values, ref writer);
            var payload = new byte[writer.WrittenLength];
            writer.CopyTo(payload);

            foreach (var canonicalLayout in new[] { false, true })
            {
                var decoded = new int?[values.Length];
                DeltaBinaryPackedDecoder.ReadNullableInt32(payload, decoded, canonicalLayout);
                if (!decoded.SequenceEqual(values.Select(static value => (int?)value)))
                    throw new InvalidOperationException(
                        $"Direct nullable Int32 decode differs with canonicalLayout={canonicalLayout}.");
            }
        }
        finally
        {
            writer.Dispose();
        }
    }

    /// <remarks>
    /// The format fixes neither the block size nor the mini-block count: the
    /// block size is a multiple of 128 and the mini-block size a multiple of 32,
    /// and that is all. Arrow uses 128/4 for INT32 but 256/4 for INT64, so
    /// requiring 128/4 rejected every int64 delta column it has ever written —
    /// all of them, on every format version, whatever the values held.
    /// </remarks>
    [Test]
    public async Task ReadsInt64ValuesWrittenWithALargerBlockSize()
    {
        var random = new Random(20260820);
        var values = new long[500];
        values[0] = long.MinValue;
        values[1] = long.MaxValue;
        values[2] = 0;
        for (var i = 3; i < values.Length; i++)
            values[i] = random.NextInt64();

        using var stream = new MemoryStream();
        using (var properties = new ParquetSharp.WriterPropertiesBuilder()
            .Compression(ParquetSharp.Compression.Uncompressed)
            .DisableDictionary()
            .Encoding(ParquetSharp.Encoding.DeltaBinaryPacked)
            .Build())
        using (var writer = new ParquetSharp.ParquetFileWriter(stream,
            [new ParquetSharp.Column<long>("value")], null, properties, null, leaveOpen: true))
        {
            using (var rowGroup = writer.AppendRowGroup())
            using (var column = rowGroup.NextColumn())
                column.LogicalWriter<long>().WriteBatch(values);
            writer.Close();
        }

        using var reader = new ParquetReader();
        reader.Reset(new MemoryStream(stream.ToArray(), writable: false));
        var read = new List<long>();
        foreach (var buffer in reader.RowGroups[0].Column<long>(reader.Schema.LeafColumns[0]))
            read.AddRange(buffer.Values.ToArray());

        await Assert.That(read).IsEquivalentTo(values);
    }

    [Test]
    [Arguments(100u, 4u)]   // not a multiple of 128
    [Arguments(0u, 4u)]     // no values per block at all
    [Arguments(128u, 5u)]   // does not divide the block
    [Arguments(128u, 0u)]   // no mini-blocks
    [Arguments(128u, 128u)] // a one-value mini-block, not a multiple of 32
    public void RejectsABlockLayoutTheFormatDoesNotAllow(uint blockSize, uint miniBlockCount)
    {
        var payload = new List<byte>();
        WriteUnsignedVarIntReference(blockSize, payload);
        WriteUnsignedVarIntReference(miniBlockCount, payload);
        WriteUnsignedVarIntReference(1, payload);
        WriteUnsignedVarIntReference(0, payload);

        try
        {
            DeltaBinaryPackedDecoder.ReadInt32(payload.ToArray(), new int[1]);
            throw new InvalidOperationException(
                $"Expected block size {blockSize} with {miniBlockCount} mini-blocks to be rejected.");
        }
        catch (CorruptParquetException)
        {
        }
    }

    /// <remarks>
    /// The bit widths of a block are read into a stack buffer, so the count a
    /// page may declare has to stop somewhere. Writers use 4; this only checks
    /// that the limit is reported rather than overrunning.
    /// </remarks>
    [Test]
    public void RejectsMoreMiniBlocksPerBlockThanTheDecoderWillHold()
    {
        var payload = new List<byte>();
        WriteUnsignedVarIntReference(32 * 128, payload);
        WriteUnsignedVarIntReference(128, payload);
        WriteUnsignedVarIntReference(1, payload);
        WriteUnsignedVarIntReference(0, payload);

        try
        {
            DeltaBinaryPackedDecoder.ReadInt32(payload.ToArray(), new int[1]);
            throw new InvalidOperationException("Expected 128 mini-blocks per block to be rejected.");
        }
        catch (NotSupportedException)
        {
        }
    }

    [Test]
    public void ReadConsumedByteCountIncludesPaddedMiniBlockBytes()
    {
        var values = new[] { 4, 11, 3, 18, 2 };
        var writer = new BufferWriter(DefaultParquetBufferPool.Shared, 1024, 1024);
        try
        {
            DeltaBinaryPackedEncoding.WriteInt32(values, ref writer);

            var payload = new byte[writer.WrittenLength + 3];
            writer.CopyTo(payload);
            payload[^3] = 0xAA;
            payload[^2] = 0xBB;
            payload[^1] = 0xCC;

            var decoded = DeltaBinaryPackedDecoder.ReadInt32(payload);
            var (_, consumed) = DeltaBinaryPackedDecoder.ReadUInt32WithConsumedBytes(payload);

            if (!decoded.SequenceEqual(values))
                throw new InvalidOperationException("Delta binary packed values did not round-trip.");
            if (consumed != writer.WrittenLength)
                throw new InvalidOperationException(
                    $"Expected consumed byte count {writer.WrittenLength}, got {consumed}.");
        }
        finally
        {
            writer.Dispose();
        }
    }

    [Test]
    public void PackedMiniBlocksMatchReferenceAcrossBitWidths()
    {
        for (var width = 0; width <= 64; width++)
        {
            var values = CreatePackedValues(width);
            var writer = new BufferWriter(DefaultParquetBufferPool.Shared, 1024, 1024);
            try
            {
                DeltaBinaryPackedEncoding.WritePackedUnsignedValues(values, width, ref writer);

                var actual = new byte[writer.WrittenLength];
                writer.CopyTo(actual);
                var expected = PackReference(values, width);
                if (!actual.SequenceEqual(expected))
                    throw new InvalidOperationException(
                        $"Packed bytes for bit width {width} do not match the reference implementation.");
            }
            finally
            {
                writer.Dispose();
            }
        }
    }

    [Test]
    public void Packed13BitMiniBlocksMatchReferenceForEveryValueInEveryLane()
    {
        const int bitWidth = 13;
        const long mask = (1L << bitWidth) - 1;
        var values = new long[32];
        var writer = new BufferWriter(DefaultParquetBufferPool.Shared, 64, 64);
        try
        {
            // Across 16,384 cases, every lane sees every 13-bit value both with and without
            // irrelevant high bits. This also verifies the direct packer's masking semantics.
            for (var testCase = 0; testCase < 2 * (mask + 1); testCase++)
            {
                var highBits = testCase > mask ? ~mask : 0;
                var seed = testCase & (int)mask;
                for (var lane = 0; lane < values.Length; lane++)
                    values[lane] = ((seed + lane * 257) & mask) | highBits;

                writer.Reset();
                DeltaBinaryPackedEncoding.WritePackedUnsignedValues(values, bitWidth, ref writer);

                var expected = PackReference(values, bitWidth);
                if (!writer.TryGetSingleWrittenSpan(out var actual) || !actual.SequenceEqual(expected))
                    throw new InvalidOperationException(
                        $"Packed 13-bit bytes do not match the reference for case {testCase}.");
            }
        }
        finally
        {
            writer.Dispose();
        }
    }

    [Test]
    public void WriteInt32MatchesReferenceAcrossBlockBoundaries()
    {
        int[] counts = [0, 1, 127, 128, 129];
        foreach (var count in counts)
        {
            var values = new int[count];
            for (var i = 0; i < values.Length; i++)
                values[i] = (i % 4) switch
                {
                    0 => int.MinValue,
                    1 => int.MaxValue,
                    2 => i * 17,
                    _ => -i * 31
                };

            AssertEncodedBytes(values, EncodeInt32Reference(values), chunkSize: 7,
                requireMultipleSegments: count == 129);
        }
    }

    [Test]
    public void WriteInt32PackingDispatchMatchesReferenceAcrossWidthsAndPartialBlocks()
    {
        int[] counts = [3, 31, 32, 33, 127, 128, 129, 257];
        for (var bitWidth = 0; bitWidth <= 33; bitWidth++)
        foreach (var count in counts)
        {
            var values = CreateValuesForBitWidth(bitWidth, count);
            AssertEncodedBytes(values, EncodeInt32Reference(values), chunkSize: 7);
        }
    }

    [Test]
    public void WriteInt32PackingDispatchMatchesReferenceAcrossMixedMiniBlockWidths()
    {
        int[] counts = [33, 127, 128, 129, 257];
        int[][] widthPatterns =
        [
            [0, 1, 2, 4],
            [4, 3, 2, 1],
            [4, 5, 4, 4],
            [5, 6, 7, 8],
            [9, 10, 11, 12],
            [4, 9, 13, 10],
            [13, 4, 13, 5],
            [12, 13, 14, 13]
        ];
        foreach (var widths in widthPatterns)
        foreach (var count in counts)
        {
            var values = CreateInt32ValuesForMiniBlockWidths(count, widths);
            AssertEncodedBytes(values, EncodeInt32Reference(values), chunkSize: 7);
        }
    }

    [Test]
    public void WriteInt64MatchesReferenceForExtremeFirstValues()
    {
        long[][] cases =
        [
            [],
            [long.MinValue],
            [long.MaxValue],
            [long.MinValue, long.MaxValue],
            [long.MaxValue, long.MinValue]
        ];
        foreach (var values in cases)
            AssertEncodedBytes(values, EncodeInt64Reference(values), chunkSize: 1);
    }

    [Test]
    public void WriteInt64MatchesReferenceAcrossBlockBoundaries()
    {
        int[] counts = [0, 1, 2, 7, 8, 9, 31, 32, 33, 127, 128, 129, 257];
        foreach (var count in counts)
        {
            var values = new long[count];
            for (var i = 0; i < values.Length; i++)
                values[i] = (i % 5) switch
                {
                    0 => long.MinValue,
                    1 => long.MaxValue,
                    2 => i * 17L,
                    3 => -i * 31L,
                    _ => unchecked((long)((ulong)i * 0x9E3779B97F4A7C15UL))
                };

            AssertEncodedBytes(values, EncodeInt64Reference(values), chunkSize: 7);
        }
    }

    [Test]
    public void WriteInt64PackingDispatchMatchesReferenceAcrossWidthsAndPartialBlocks()
    {
        int[] counts = [3, 31, 32, 33, 127, 128, 129, 257];
        for (var bitWidth = 0; bitWidth <= 64; bitWidth++)
        foreach (var count in counts)
        {
            var values = CreateInt64ValuesForBitWidth(bitWidth, count);
            AssertEncodedBytes(values, EncodeInt64Reference(values), chunkSize: 7);
        }
    }

    [Test]
    public void WriteInt64PackingDispatchMatchesReferenceAcrossMixedMiniBlockWidths()
    {
        int[] counts = [33, 127, 128, 129, 257];
        int[][] widthPatterns =
        [
            [0, 1, 2, 4],
            [4, 3, 2, 1],
            [4, 5, 4, 4],
            [5, 6, 7, 8],
            [9, 10, 11, 12],
            [4, 9, 13, 10],
            [13, 4, 13, 5],
            [12, 13, 14, 13],
            [64, 0, 1, 4]
        ];
        foreach (var widths in widthPatterns)
        foreach (var count in counts)
        {
            var values = CreateInt64ValuesForMiniBlockWidths(count, widths);
            AssertEncodedBytes(values, EncodeInt64Reference(values), chunkSize: 7);
        }
    }

    [Test]
    public void WriteInt32PreservesKnownPayload()
    {
        int[] values = [4, 11, 3, 18, 2];
        var expected = Convert.FromHexString(
            "80010405081F05000000177D000000000000000000000000000000000000");
        AssertEncodedBytes(values, expected, chunkSize: 7, requireMultipleSegments: true);
    }

    [Test]
    public void ReadInt32RoundTripsPartialBlocksAndSignedExtremes()
    {
        int[] counts = [0, 1, 2, 7, 8, 9, 31, 32, 33, 127, 128, 129, 257];
        foreach (var count in counts)
        {
            var values = new int[count];
            for (var i = 0; i < values.Length; i++)
                values[i] = (i % 4) switch
                {
                    0 => int.MinValue,
                    1 => int.MaxValue,
                    2 => i,
                    _ => -i
                };

            var writer = new BufferWriter(DefaultParquetBufferPool.Shared, 4096, 4096);
            try
            {
                DeltaBinaryPackedEncoding.WriteInt32(values, ref writer);

                var payload = new byte[writer.WrittenLength];
                writer.CopyTo(payload);
                var decoded = DeltaBinaryPackedDecoder.ReadInt32(payload);
                if (!decoded.SequenceEqual(values))
                    throw new InvalidOperationException(
                        $"Delta binary packed values did not round-trip for count {count}.");
            }
            finally
            {
                writer.Dispose();
            }
        }
    }

    [Test]
    public void ReadInt32IntoSpanRoundTripsPackedWidthsAndPartialMiniBlocks()
    {
        int[] counts = [2, 31, 32, 33, 127, 128, 129, 257, 4097];
        foreach (var count in counts)
            for (var distribution = 0; distribution < 3; distribution++)
            {
                var values = CreateDecodeValues(count, distribution);
                var writer = new BufferWriter(DefaultParquetBufferPool.Shared, 4096, 4096);
                try
                {
                    DeltaBinaryPackedEncoding.WriteInt32(values, ref writer);
                    var payload = new byte[writer.WrittenLength];
                    writer.CopyTo(payload);
                    var decoded = new int[values.Length];

                    var consumed = DeltaBinaryPackedDecoder.ReadInt32(payload, decoded);

                    if (!decoded.SequenceEqual(values))
                        throw new InvalidOperationException(
                            $"Span decode did not round-trip {count} values for distribution {distribution}.");
                    if (consumed != payload.Length)
                        throw new InvalidOperationException(
                            $"Span decode consumed {consumed} bytes instead of {payload.Length}.");
                }
                finally
                {
                    writer.Dispose();
                }
            }
    }

    [Test]
    public void ReadInt32IntoSpanRoundTripsEveryValidPackedWidth()
    {
        int[] counts = [5, 8, 9, 31, 32, 33, 129];
        for (var bitWidth = 0; bitWidth <= 33; bitWidth++)
        foreach (var count in counts)
        {
            var values = CreateValuesForBitWidth(bitWidth, count);
            var writer = new BufferWriter(DefaultParquetBufferPool.Shared, 4096, 4096);
            try
            {
                DeltaBinaryPackedEncoding.WriteInt32(values, ref writer);
                var payload = new byte[writer.WrittenLength];
                writer.CopyTo(payload);
                var decoded = new int[values.Length];

                var consumed = DeltaBinaryPackedDecoder.ReadInt32(payload, decoded);

                if (!decoded.SequenceEqual(values))
                    throw new InvalidOperationException(
                        $"Span decode did not round-trip bit width {bitWidth} with {count} values.");
                if (consumed != payload.Length)
                    throw new InvalidOperationException(
                        $"Bit width {bitWidth} with {count} values consumed {consumed} bytes " +
                        $"instead of {payload.Length}.");
            }
            finally
            {
                writer.Dispose();
            }
        }
    }

    [Test]
    public void ReadInt64IntoSpanRoundTripsEveryValidPackedWidthAndPartialMiniBlock()
    {
        int[] counts = [2, 5, 31, 32, 33, 127, 128, 129, 257];
        for (var bitWidth = 0; bitWidth <= 64; bitWidth++)
        foreach (var count in counts)
        {
            var values = CreateInt64ValuesForBitWidth(bitWidth, count);
            var writer = new BufferWriter(DefaultParquetBufferPool.Shared, 4096, 4096);
            try
            {
                DeltaBinaryPackedEncoding.WriteInt64(values, ref writer);
                var payload = new byte[writer.WrittenLength];
                writer.CopyTo(payload);
                var decoded = new long[values.Length];

                var consumed = DeltaBinaryPackedDecoder.ReadInt64(payload, decoded);

                if (!decoded.SequenceEqual(values))
                    throw new InvalidOperationException(
                        $"Int64 span decode did not round-trip bit width {bitWidth} with {count} values.");
                if (consumed != payload.Length)
                    throw new InvalidOperationException(
                        $"Int64 bit width {bitWidth} with {count} values consumed {consumed} bytes " +
                        $"instead of {payload.Length}.");
            }
            finally
            {
                writer.Dispose();
            }
        }
    }

    [Test]
    public void ReadInt64ArrayRoundTripsMixedMiniBlockWidths()
    {
        int[][] widthPatterns =
        [
            [0, 1, 2, 4],
            [4, 3, 2, 1],
            [4, 5, 4, 4],
            [5, 6, 7, 8],
            [13, 4, 13, 5],
            [12, 13, 14, 13],
            [8, 9, 15, 16],
            [56, 57, 63, 64]
        ];
        foreach (var widths in widthPatterns)
        {
            var values = CreateInt64ValuesForMiniBlockWidths(257, widths);
            var writer = new BufferWriter(DefaultParquetBufferPool.Shared, 4096, 4096);
            try
            {
                DeltaBinaryPackedEncoding.WriteInt64(values, ref writer);
                var payload = new byte[writer.WrittenLength];
                writer.CopyTo(payload);

                var decoded = DeltaBinaryPackedDecoder.ReadInt64(payload);

                if (!decoded.SequenceEqual(values))
                    throw new InvalidOperationException(
                        $"Int64 array decode did not round-trip widths {string.Join(", ", widths)}.");
            }
            finally
            {
                writer.Dispose();
            }
        }
    }

    [Test]
    public void ReadInt64RejectsTruncatedFullMiniBlocksOnFastUnpackWidths()
    {
        int[] bitWidths = [1, 3, 8, 9, 15, 16];
        foreach (var bitWidth in bitWidths)
        {
            var payload = CreateDeclaredWidthPayload(bitWidth, valueCount: 33);
            Array.Resize(ref payload, payload.Length - 1);

            try
            {
                DeltaBinaryPackedDecoder.ReadInt64(payload, new long[33]);
                throw new InvalidOperationException(
                    $"Expected truncated Int64 bit width {bitWidth} payload to be rejected.");
            }
            catch (CorruptParquetException)
            {
            }
        }
    }

    [Test]
    public void ReadInt64RejectsBitWidthAbove64BeforeReadingPackedData()
    {
        var payload = CreateDeclaredWidthPayload(bitWidth: 65, valueCount: 33);
        try
        {
            DeltaBinaryPackedDecoder.ReadInt64(payload, new long[33]);
            throw new InvalidOperationException("Expected Int64 bit width 65 to be rejected.");
        }
        catch (CorruptParquetException)
        {
        }
    }

    [Test]
    public void ReadInt32AcceptsDeclaredPackedWidthsZeroThrough64()
    {
        for (var bitWidth = 0; bitWidth <= 64; bitWidth++)
        {
            var payload = CreateDeclaredWidthPayload(bitWidth, valueCount: 33);
            var decoded = new int[33];

            var consumed = DeltaBinaryPackedDecoder.ReadInt32(payload, decoded);

            if (decoded.Any(static value => value != 0))
                throw new InvalidOperationException(
                    $"Declared bit width {bitWidth} did not decode its zero residuals.");
            if (consumed != payload.Length)
                throw new InvalidOperationException(
                    $"Declared bit width {bitWidth} consumed {consumed} bytes instead of {payload.Length}.");
        }
    }

    [Test]
    public void ReadInt32RejectsTruncatedFullMiniBlocksOnFastUnpackWidths()
    {
        int[] bitWidths = [1, 3, 8, 9, 15, 16];
        foreach (var bitWidth in bitWidths)
        {
            var payload = CreateDeclaredWidthPayload(bitWidth, valueCount: 33);
            Array.Resize(ref payload, payload.Length - 1);

            try
            {
                DeltaBinaryPackedDecoder.ReadInt32(payload, new int[33]);
                throw new InvalidOperationException(
                    $"Expected truncated bit width {bitWidth} payload to be rejected.");
            }
            catch (CorruptParquetException)
            {
            }
        }
    }

    [Test]
    public void ReadInt32RejectsBitWidthAbove64BeforeReadingPackedData()
    {
        var payload = CreateDeclaredWidthPayload(bitWidth: 65, valueCount: 33);
        try
        {
            DeltaBinaryPackedDecoder.ReadInt32(payload, new int[33]);
            throw new InvalidOperationException("Expected bit width 65 to be rejected.");
        }
        catch (CorruptParquetException)
        {
        }
    }

    /// <remarks>
    /// The format defines these additions as modular: "Subtractions in steps 1)
    /// and 3) may incur signed arithmetic overflow, and so will the
    /// corresponding additions when decoding. Based on the assumption of a 2's
    /// complement representation, this works OK." So a running sum leaving the
    /// Int32 range is not a corruption signal — it is how a delta from
    /// Int32.MaxValue to Int32.MinValue is spelled — and rejecting it made every
    /// Arrow-written INT32 delta column unreadable once its values spanned the
    /// type.
    /// </remarks>
    [Test]
    public async Task ReadInt32WrapsAnOverflowingSumInEveryVectorLaneAndTail()
    {
        int[] overflowIndexes = [0, 3, 4, 7, 8, 15, 16, 23, 24, 30, 31];
        foreach (var overflowIndex in overflowIndexes)
        {
            var payload = new List<byte>();
            WriteUnsignedVarIntReference(128, payload);
            WriteUnsignedVarIntReference(4, payload);
            WriteUnsignedVarIntReference(33, payload);
            WriteUnsignedVarIntReference(uint.MaxValue - 1, payload);

            var deltas = new long[128];
            deltas[overflowIndex] = 1;
            if (overflowIndex + 1 < 32)
                deltas[overflowIndex + 1] = -1;
            Array.Fill(deltas, -1, 32, deltas.Length - 32);
            WriteDeltaBlockReference(deltas, -1, payload);

            var destination = new int[33];
            DeltaBinaryPackedDecoder.ReadInt32(payload.ToArray(), destination);

            // The run sits at Int32.MaxValue, steps once past it — which wraps to
            // Int32.MinValue — and the matching -1  step brings it back.
            for (var i = 0; i < destination.Length; i++)
                await Assert.That(destination[i])
                    .IsEqualTo(i == overflowIndex + 1 ? int.MinValue : int.MaxValue);
        }
    }

    [Test]
    public async Task ReadInt32WrapsASumThatLeavesTheInt64Range()
    {
        var payload = new List<byte>();
        WriteUnsignedVarIntReference(128, payload);
        WriteUnsignedVarIntReference(4, payload);
        WriteUnsignedVarIntReference(5, payload);
        WriteUnsignedVarIntReference(0, payload);

        var deltas = new long[128];
        deltas[0] = long.MinValue;
        deltas[1] = long.MaxValue;
        deltas[2] = long.MinValue;
        deltas[3] = long.MaxValue;
        Array.Fill(deltas, long.MaxValue, 4, deltas.Length - 4);
        WriteDeltaBlockReference(deltas, long.MaxValue, payload);

        var destination = new int[5];
        DeltaBinaryPackedDecoder.ReadInt32(payload.ToArray(), destination);

        // No conformant encoder produces Int64-wide deltas for an INT32 column, so
        // this is a corrupt payload. It still has to decode to something definite
        // rather than crash, and modular arithmetic is what the format specifies —
        // there is no way to tell it apart from a legitimate wrap.
        await Assert.That(destination).IsEquivalentTo(new[] { 0, 0, -1, -1, -2 });
    }

    [Test]
    public async Task ReadInt32WrapsAnOverflowingSumWithinAMiniBlock()
    {
        var payload = new List<byte>();
        WriteUnsignedVarIntReference(128, payload);
        WriteUnsignedVarIntReference(4, payload);
        WriteUnsignedVarIntReference(3, payload);
        WriteUnsignedVarIntReference(0, payload);

        var deltas = new long[128];
        deltas[0] = (long)int.MaxValue + 1;
        deltas[1] = int.MinValue;
        Array.Fill(deltas, (long)int.MinValue, 2, deltas.Length - 2);
        WriteDeltaBlockReference(deltas, int.MinValue, payload);

        var destination = new int[3];
        DeltaBinaryPackedDecoder.ReadInt32(payload.ToArray(), destination);

        // 0 -> Int32.MinValue -> 0, which is what those two deltas mean under
        // two's complement and exactly what an encoder would emit for it.
        await Assert.That(destination).IsEquivalentTo(new[] { 0, int.MinValue, 0 });
    }

    /// <remarks>
    /// Arrow computes its deltas with wrapping 32-bit subtraction, so a column
    /// holding values from both ends of the type is ordinary output, not an edge
    /// case. Plank's own encoder widens instead, which is why round-tripping
    /// through it — the only INT32 delta coverage there used to be — never
    /// produced a payload the reader rejected.
    /// </remarks>
    [Test]
    public async Task ReadsFullRangeInt32ValuesWrittenByAnotherImplementation()
    {
        var random = new Random(20260820);
        var values = new int[300];
        values[0] = int.MinValue;
        values[1] = int.MaxValue;
        values[2] = int.MinValue;
        for (var i = 3; i < values.Length; i++)
            values[i] = random.Next(int.MinValue, int.MaxValue);

        using var stream = new MemoryStream();
        using (var properties = new ParquetSharp.WriterPropertiesBuilder()
            .Compression(ParquetSharp.Compression.Uncompressed)
            .DisableDictionary()
            .Encoding(ParquetSharp.Encoding.DeltaBinaryPacked)
            .Build())
        using (var writer = new ParquetSharp.ParquetFileWriter(stream,
            [new ParquetSharp.Column<int>("value")], null, properties, null, leaveOpen: true))
        {
            using (var rowGroup = writer.AppendRowGroup())
            using (var column = rowGroup.NextColumn())
                column.LogicalWriter<int>().WriteBatch(values);
            writer.Close();
        }

        using var reader = new ParquetReader();
        reader.Reset(new MemoryStream(stream.ToArray(), writable: false));
        var read = new List<int>();
        foreach (var buffer in reader.RowGroups[0].Column<int>(reader.Schema.LeafColumns[0]))
            read.AddRange(buffer.Values.ToArray());

        await Assert.That(read).IsEquivalentTo(values);
    }

    static long[] CreatePackedValues(int bitWidth)
    {
        var values = new long[32];
        var mask = bitWidth switch
        {
            0 => 0UL,
            64 => ulong.MaxValue,
            _ => (1UL << bitWidth) - 1
        };
        for (var i = 0; i < values.Length; i++)
        {
            var mixed = unchecked((ulong)i * 0x9E3779B97F4A7C15UL);
            values[i] = unchecked((long)(mixed & mask));
        }

        return values;
    }

    static long[] CreateInt64ValuesForBitWidth(int bitWidth, int count)
    {
        var values = new long[count];
        for (var i = 1; i < values.Length; i++)
        {
            long delta;
            if (bitWidth == 0)
            {
                delta = 7;
            }
            else if (bitWidth == 64)
            {
                delta = (i & 1) != 0 ? long.MinValue : long.MaxValue;
            }
            else
            {
                const long minDelta = -8;
                var highResidual = 1UL << (bitWidth - 1);
                delta = (i & 1) != 0 ? minDelta : unchecked(minDelta + (long)highResidual);
            }

            values[i] = unchecked(values[i - 1] + delta);
        }

        return values;
    }

    static int[] CreateInt32ValuesForMiniBlockWidths(int count, ReadOnlySpan<int> widths)
    {
        var values = new int[count];
        for (var i = 1; i < values.Length; i++)
        {
            var miniBlock = ((i - 1) / 32) & 3;
            var width = widths[miniBlock];
            var miniBlockOffset = (i - 1) & 31;
            var residual = miniBlockOffset == 1 && width > 0 ? 1 << (width - 1) : 0;
            var delta = -9 + residual;
            values[i] = checked(values[i - 1] + delta);
        }

        return values;
    }

    static long[] CreateInt64ValuesForMiniBlockWidths(int count, ReadOnlySpan<int> widths)
    {
        var values = new long[count];
        for (var i = 1; i < values.Length; i++)
        {
            var miniBlock = ((i - 1) / 32) & 3;
            var width = widths[miniBlock];
            var miniBlockOffset = (i - 1) & 31;
            var residual = miniBlockOffset switch
            {
                0 => 0UL,
                1 when width == 64 => 1UL << 63,
                1 when width > 0 => 1UL << (width - 1),
                _ => 0UL
            };
            var delta = unchecked(-9L + (long)residual);
            values[i] = unchecked(values[i - 1] + delta);
        }

        return values;
    }

    static byte[] CreateDeclaredWidthPayload(int bitWidth, int valueCount)
    {
        var payload = new List<byte>();
        WriteUnsignedVarIntReference(128, payload);
        WriteUnsignedVarIntReference(4, payload);
        WriteUnsignedVarIntReference((ulong)valueCount, payload);
        WriteUnsignedVarIntReference(0, payload);
        WriteUnsignedVarIntReference(0, payload);
        payload.Add(checked((byte)bitWidth));
        payload.Add(0);
        payload.Add(0);
        payload.Add(0);
        payload.AddRange(new byte[checked(bitWidth * 4)]);
        return payload.ToArray();
    }

    static int[] CreateDecodeValues(int count, int distribution)
    {
        var values = new int[count];
        switch (distribution)
        {
            case 0:
                for (var i = 0; i < values.Length; i++)
                    values[i] = i * 3;
                break;
            case 1:
                for (var i = 1; i < values.Length; i++)
                    values[i] = values[i - 1] + (i % 7 == 0 ? -3 : 4);
                break;
            default:
                new Random(42 + count).NextBytes(
                    System.Runtime.InteropServices.MemoryMarshal.AsBytes(values.AsSpan()));
                break;
        }
        return values;
    }

    static int[] CreateValuesForBitWidth(int bitWidth, int count)
    {
        var values = new int[count];
        if (bitWidth == 0)
        {
            for (var i = 1; i < values.Length; i++)
                values[i] = values[i - 1] + 3;
            return values;
        }

        if (bitWidth <= 31)
        {
            var lowDelta = -(1L << (bitWidth - 1));
            var highDelta = (1L << (bitWidth - 1)) - 1;
            for (var i = 1; i < values.Length; i++)
                values[i] = checked((int)(values[i - 1] + (i % 2 == 0 ? highDelta : lowDelta)));
            return values;
        }

        if (bitWidth == 32)
        {
            values[0] = int.MaxValue;
            for (var i = 1; i < values.Length; i++)
                values[i] = checked((int)(values[i - 1] +
                    (i % 2 == 0 ? (long)int.MaxValue : int.MinValue)));
            return values;
        }

        for (var i = 0; i < values.Length; i++)
            values[i] = i % 2 == 0 ? int.MinValue : int.MaxValue;
        return values;
    }

    static void AssertEncodedBytes(ReadOnlySpan<int> values, byte[] expected, uint chunkSize,
        bool requireMultipleSegments = false)
    {
        var writer = new BufferWriter(DefaultParquetBufferPool.Shared, chunkSize, chunkSize);
        try
        {
            DeltaBinaryPackedEncoding.WriteInt32(values, ref writer);
            AssertEncodedBytes(ref writer, expected, requireMultipleSegments);
        }
        finally
        {
            writer.Dispose();
        }
    }

    static void AssertEncodedBytes(ReadOnlySpan<long> values, byte[] expected, uint chunkSize)
    {
        var writer = new BufferWriter(DefaultParquetBufferPool.Shared, chunkSize, chunkSize);
        try
        {
            DeltaBinaryPackedEncoding.WriteInt64(values, ref writer);
            AssertEncodedBytes(ref writer, expected, requireMultipleSegments: false);
        }
        finally
        {
            writer.Dispose();
        }
    }

    static void AssertEncodedBytes(ref BufferWriter writer, byte[] expected, bool requireMultipleSegments)
    {
        var actual = new byte[writer.WrittenLength];
        writer.CopyTo(actual);
        if (!actual.SequenceEqual(expected))
            throw new InvalidOperationException(
                $"Expected {Convert.ToHexString(expected)}, got {Convert.ToHexString(actual)}.");
        if (requireMultipleSegments && writer.TryGetSingleWrittenSpan(out _))
            throw new InvalidOperationException("Expected the encoded payload to span multiple writer segments.");
    }

    static byte[] EncodeInt32Reference(ReadOnlySpan<int> values)
    {
        var output = new List<byte>();
        WriteUnsignedVarIntReference(128, output);
        WriteUnsignedVarIntReference(4, output);
        WriteUnsignedVarIntReference((ulong)values.Length, output);

        if (values.Length == 0)
        {
            WriteUnsignedVarIntReference(0, output);
            return output.ToArray();
        }

        WriteUnsignedVarIntReference((uint)((values[0] << 1) ^ (values[0] >> 31)), output);
        if (values.Length == 1)
            return output.ToArray();

        var deltas = new long[128];
        var previous = values[0];
        var index = 1;
        while (index < values.Length)
        {
            var count = Math.Min(deltas.Length, values.Length - index);
            var minDelta = long.MaxValue;
            for (var i = 0; i < count; i++)
            {
                var current = values[index + i];
                var delta = (long)current - previous;
                previous = current;
                deltas[i] = delta;
                minDelta = Math.Min(minDelta, delta);
            }

            Array.Fill(deltas, minDelta, count, deltas.Length - count);
            WriteDeltaBlockReference(deltas, minDelta, output);
            index += count;
        }

        return output.ToArray();
    }

    static byte[] EncodeInt64Reference(ReadOnlySpan<long> values)
    {
        var output = new List<byte>();
        WriteUnsignedVarIntReference(128, output);
        WriteUnsignedVarIntReference(4, output);
        WriteUnsignedVarIntReference((ulong)values.Length, output);

        if (values.Length == 0)
        {
            WriteUnsignedVarIntReference(0, output);
            return output.ToArray();
        }

        WriteUnsignedVarIntReference((ulong)((values[0] << 1) ^ (values[0] >> 63)), output);
        if (values.Length == 1)
            return output.ToArray();

        var deltas = new long[128];
        var previous = values[0];
        var index = 1;
        while (index < values.Length)
        {
            var count = Math.Min(deltas.Length, values.Length - index);
            var minDelta = long.MaxValue;
            for (var i = 0; i < count; i++)
            {
                var current = values[index + i];
                var delta = current - previous;
                previous = current;
                deltas[i] = delta;
                minDelta = Math.Min(minDelta, delta);
            }

            Array.Fill(deltas, minDelta, count, deltas.Length - count);
            WriteDeltaBlockReference(deltas, minDelta, output);
            index += count;
        }

        return output.ToArray();
    }

    static void WriteDeltaBlockReference(long[] deltas, long minDelta, List<byte> output)
    {
        WriteUnsignedVarIntReference((ulong)((minDelta << 1) ^ (minDelta >> 63)), output);

        var bitWidths = new byte[4];
        for (var block = 0; block < bitWidths.Length; block++)
        {
            var start = block * 32;
            ulong max = 0;
            for (var i = 0; i < 32; i++)
            {
                var normalized = (ulong)(deltas[start + i] - minDelta);
                deltas[start + i] = (long)normalized;
                max = Math.Max(max, normalized);
            }

            bitWidths[block] = (byte)(64 - System.Numerics.BitOperations.LeadingZeroCount(max));
            output.Add(bitWidths[block]);
        }

        for (var block = 0; block < bitWidths.Length; block++)
            output.AddRange(PackReference(deltas.AsSpan(block * 32, 32), bitWidths[block]));
    }

    static void WriteUnsignedVarIntReference(ulong value, List<byte> output)
    {
        while (value >= 0x80)
        {
            output.Add((byte)(value | 0x80));
            value >>= 7;
        }

        output.Add((byte)value);
    }

    static byte[] PackReference(ReadOnlySpan<long> values, int bitWidth)
    {
        var destination = new byte[checked((values.Length * bitWidth + 7) >> 3)];
        var mask = bitWidth switch
        {
            0 => 0UL,
            64 => ulong.MaxValue,
            _ => (1UL << bitWidth) - 1
        };
        var bitOffset = 0;
        for (var i = 0; i < values.Length; i++)
        {
            var value = unchecked((ulong)values[i]) & mask;
            for (var bit = 0; bit < bitWidth; bit++)
                if (((value >> bit) & 1) != 0)
                    destination[(bitOffset + bit) >> 3] |= (byte)(1 << ((bitOffset + bit) & 7));
            bitOffset += bitWidth;
        }

        return destination;
    }
}
