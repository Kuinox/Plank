using Plank.Reading;
using Plank.Schema;
using Plank.Writing;
using Plank.Writing.Encoding;

namespace Plank.Tests.Mutation;

/// <summary>
/// Tests targeting surviving mutants in DeltaBinaryPackedEncoding.cs:
/// - line 58/99: if (delta &lt; minDelta) — min delta tracking
/// - line 217: mask = bitWidth == 64 ? ulong.MaxValue : ...
/// - lines 213/228/263: bit width calculations
/// </summary>
public class DeltaBinaryPackedEncodingTests
{
    static int[] EncodeDecodeInt32(int[] values)
    {
        var writer = new BufferWriter(DefaultParquetBufferPool.Shared, 4096, 4096);
        DeltaBinaryPackedEncoding.WriteInt32(values, ref writer);
        var payload = new byte[writer.WrittenLength];
        writer.CopyTo(payload);
        return DeltaBinaryPackedDecoder.ReadInt32(payload);
    }

    static long[] EncodeDecodeInt64(long[] values)
    {
        var writer = new BufferWriter(DefaultParquetBufferPool.Shared, 4096, 4096);
        DeltaBinaryPackedEncoding.WriteInt64(values, ref writer);
        var payload = new byte[writer.WrittenLength];
        writer.CopyTo(payload);
        return DeltaBinaryPackedDecoder.ReadInt64(payload);
    }

    // ──────────────── MinDelta tracking (lines 58, 99) ────────────────

    [Test]
    public void Int32_MinDeltaIsFirst_RoundTrip()
    {
        // First delta is the minimum: [10, 3, 8, 15] → deltas [-7, 5, 7], minDelta = -7
        var values = new int[] { 10, 3, 8, 15 };
        ClassicAssert.AreEqual(values, EncodeDecodeInt32(values));
    }

    [Test]
    public void Int32_MinDeltaIsLast_RoundTrip()
    {
        // Last delta is the minimum: [0, 5, 12, 4] → deltas [5, 7, -8], minDelta = -8
        var values = new int[] { 0, 5, 12, 4 };
        ClassicAssert.AreEqual(values, EncodeDecodeInt32(values));
    }

    [Test]
    public void Int32_MinDeltaIsMiddle_RoundTrip()
    {
        // Middle delta is the minimum: [0, 10, 3, 7, 12] → deltas [10, -7, 4, 5], minDelta = -7
        var values = new int[] { 0, 10, 3, 7, 12 };
        ClassicAssert.AreEqual(values, EncodeDecodeInt32(values));
    }

    [Test]
    public void Int32_AllSameDeltas_RoundTrip()
    {
        // Constant deltas: arithmetic sequence
        var values = Enumerable.Range(0, 20).Select(i => i * 3).ToArray();
        ClassicAssert.AreEqual(values, EncodeDecodeInt32(values));
    }

    [Test]
    public void Int32_NegativeMinDelta_LargeSpread_RoundTrip()
    {
        // Large negative delta in the middle
        var values = new int[] { 1000, 2000, 100, 500, 800 };
        ClassicAssert.AreEqual(values, EncodeDecodeInt32(values));
    }

    [Test]
    public void Int32_LargePositiveThenNegative_RoundTrip()
    {
        // [0, int.MaxValue/2, 0] — large positive then very large negative delta
        var values = new int[] { 0, int.MaxValue / 2, 0 };
        ClassicAssert.AreEqual(values, EncodeDecodeInt32(values));
    }

    // ──────────────── Single-value edge case (line 42) ────────────────

    [Test]
    public void Int32_Single_EarlyReturn()
    {
        // Length == 1 → early return after writing first value
        Assert.That(EncodeDecodeInt32([42]), Is.EqualTo(new[] {42}));
    }

    [Test]
    public void Int64_Single_EarlyReturn()
    {
        Assert.That(EncodeDecodeInt64([100L]), Is.EqualTo(new[] {100L}));
    }

    // ──────────────── Int64 MinDelta (lines 99) ────────────────

    [Test]
    public void Int64_MinDeltaIsFirst_RoundTrip()
    {
        var values = new long[] { 1_000_000L, 100_000L, 500_000L, 800_000L };
        ClassicAssert.AreEqual(values, EncodeDecodeInt64(values));
    }

    [Test]
    public void Int64_MinDeltaIsLast_RoundTrip()
    {
        var values = new long[] { 0L, 100L, 200L, 50L };
        ClassicAssert.AreEqual(values, EncodeDecodeInt64(values));
    }

    [Test]
    public void Int64_AlternatingLargeSmall_RoundTrip()
    {
        var values = new long[] { 0L, 1_000_000_000L, 1L, 2_000_000_000L };
        ClassicAssert.AreEqual(values, EncodeDecodeInt64(values));
    }

    // ──────────────── Bit width = 64 mask (line 217) ────────────────

    [Test]
    public void Int32_WideRangeDeltasRequire32Bits_RoundTrip()
    {
        // Deltas span nearly the full int32 range
        var values = new int[] { 0, int.MaxValue, 0, int.MinValue, 0 };
        ClassicAssert.AreEqual(values, EncodeDecodeInt32(values));
    }

    [Test]
    public void Int64_WideRangeDeltas_RoundTrip()
    {
        // Deltas that require many bits
        var values = new long[] { 0L, long.MaxValue / 2, -long.MaxValue / 2, long.MaxValue / 4 };
        ClassicAssert.AreEqual(values, EncodeDecodeInt64(values));
    }

    // ──────────────── GetBitWidth (line 263) ────────────────

    [Test]
    public void Int32_AllZeroDeltas_MinBitWidth_RoundTrip()
    {
        // All same values → all deltas = 0 → minDelta = 0 → all adjusted = 0 → bitWidth = 0
        var values = Enumerable.Repeat(42, 20).ToArray();
        ClassicAssert.AreEqual(values, EncodeDecodeInt32(values));
    }

    [Test]
    public void Int32_OneBitDeltas_RoundTrip()
    {
        // Deltas are all 0 or 1 (after adjustment): bitWidth = 1
        // Ascending values where most deltas are 1, one is 0
        var values = new int[] { 0, 1, 2, 2, 3, 4, 5, 6 };
        ClassicAssert.AreEqual(values, EncodeDecodeInt32(values));
    }

    // ──────────────── Via Parquet encoding roundtrip ────────────────

    [Test]
    public void Int32_DeltaEncoding_MinDelta_ViaParquet()
    {
        var schema = new ParquetSchema([new Column("v", ParquetPhysicalType.Int32,
            new ColumnOptions(encodings: [EncodingKind.DeltaBinaryPacked]))]);
        // Values with clear minimum delta in the middle
        var values = new int[] { 100, 200, 150, 300 }; // deltas: 100, -50, 150; minDelta = -50
        using var ms = new MemoryStream();
        var writer = schema.CreateWriter(ms, new ParquetWriterOptions { Compression = CompressionKind.None });
        var col = writer.CreateSerializedColumn<int>(schema.Columns[0]);
        col.Serialize(values);
        writer.StartRowGroup().Write(col);
        writer.CloseFile();
        ClassicAssert.AreEqual(values, ReadAll<int>(ms.ToArray(), schema));
    }

    [Test]
    public void Int64_DeltaEncoding_ViaParquet()
    {
        var schema = new ParquetSchema([new Column("v", ParquetPhysicalType.Int64,
            new ColumnOptions(encodings: [EncodingKind.DeltaBinaryPacked]))]);
        var values = new long[] { 0L, 1000L, 500L, 2000L, 100L };
        using var ms = new MemoryStream();
        var writer = schema.CreateWriter(ms, new ParquetWriterOptions { Compression = CompressionKind.None });
        var col = writer.CreateSerializedColumn<long>(schema.Columns[0]);
        col.Serialize(values);
        writer.StartRowGroup().Write(col);
        writer.CloseFile();
        ClassicAssert.AreEqual(values, ReadAll<long>(ms.ToArray(), schema));
    }

    static T[] ReadAll<T>(byte[] data, ParquetSchema schema)
    {
        var src = new MemoryReadSource(data);
        using var reader = schema.CreateReader(src);
        var results = new List<T>();
        foreach (var tok in reader.EnumerateRowGroups())
        {
            using var rg = reader.OpenRowGroup(src, tok);
            foreach (var page in rg.Column<T>(schema.Columns[0]).Pages)
                foreach (var v in page.Values.Span)
                    results.Add(v);
        }
        return results.ToArray();
    }
}
