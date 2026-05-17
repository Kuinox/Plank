using Plank.Reading;
using Plank.Writing;
using Plank.Writing.Encoding;

namespace Plank.StrykerTests;

public class DeltaBinaryPackedDecoderTests
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

    [Fact]
    public void Int32_Empty_RoundTrip()
    {
        var result = EncodeDecodeInt32([]);
        Assert.Empty(result);
    }

    [Fact]
    public void Int32_Single_RoundTrip()
    {
        var result = EncodeDecodeInt32([42]);
        Assert.Equal([42], result);
    }

    [Fact]
    public void Int32_Ascending_RoundTrip()
    {
        var values = Enumerable.Range(0, 200).ToArray();
        var result = EncodeDecodeInt32(values);
        Assert.Equal(values, result);
    }

    [Fact]
    public void Int32_Descending_RoundTrip()
    {
        var values = Enumerable.Range(0, 200).Select(i => 200 - i).ToArray();
        var result = EncodeDecodeInt32(values);
        Assert.Equal(values, result);
    }

    [Fact]
    public void Int32_Constant_RoundTrip()
    {
        var values = Enumerable.Repeat(99, 150).ToArray();
        var result = EncodeDecodeInt32(values);
        Assert.Equal(values, result);
    }

    [Fact]
    public void Int32_Negative_RoundTrip()
    {
        var values = new int[] { -5, -10, -3, -100, 0, 1, -1 };
        var result = EncodeDecodeInt32(values);
        Assert.Equal(values, result);
    }

    [Fact]
    public void Int32_ExactlyOneBlock_RoundTrip()
    {
        // BlockSize = 128
        var values = Enumerable.Range(1, 128).ToArray();
        var result = EncodeDecodeInt32(values);
        Assert.Equal(values, result);
    }

    [Fact]
    public void Int32_MoreThanOneBlock_RoundTrip()
    {
        var values = Enumerable.Range(1, 257).ToArray();
        var result = EncodeDecodeInt32(values);
        Assert.Equal(values, result);
    }

    [Fact]
    public void Int32_MinAndMax_RoundTrip()
    {
        var values = new int[] { int.MinValue, 0, int.MaxValue };
        var result = EncodeDecodeInt32(values);
        Assert.Equal(values, result);
    }

    [Fact]
    public void Int64_Empty_RoundTrip()
    {
        var result = EncodeDecodeInt64([]);
        Assert.Empty(result);
    }

    [Fact]
    public void Int64_Single_RoundTrip()
    {
        var result = EncodeDecodeInt64([1_000_000_000_000L]);
        Assert.Equal([1_000_000_000_000L], result);
    }

    [Fact]
    public void Int64_Ascending_RoundTrip()
    {
        var values = Enumerable.Range(0, 200).Select(i => (long)i * 1_000_000L).ToArray();
        var result = EncodeDecodeInt64(values);
        Assert.Equal(values, result);
    }

    [Fact]
    public void Int32_ReadIntoDestination_PopulatesValues()
    {
        var values = Enumerable.Range(0, 50).ToArray();
        var writer = new BufferWriter(DefaultParquetBufferPool.Shared, 4096, 4096);
        DeltaBinaryPackedEncoding.WriteInt32(values, ref writer);
        var payload = new byte[writer.WrittenLength];
        writer.CopyTo(payload);
        // destination must have exactly valueCount elements
        var dest = new int[50];
        var consumedBytes = DeltaBinaryPackedDecoder.ReadInt32(payload, dest);
        Assert.True(consumedBytes > 0);
        Assert.Equal(values, dest);
    }

    [Fact]
    public void Int32_ReadIntoDestination_EmptyDestination_Succeeds()
    {
        var writer = new BufferWriter(DefaultParquetBufferPool.Shared, 4096, 4096);
        DeltaBinaryPackedEncoding.WriteInt32([], ref writer);
        var payload = new byte[writer.WrittenLength];
        writer.CopyTo(payload);
        // empty destination matches empty encoded payload
        var consumedBytes = DeltaBinaryPackedDecoder.ReadInt32(payload, Span<int>.Empty);
        Assert.True(consumedBytes > 0); // still consumed the header
    }

    // ──────────────── ReadUInt32WithConsumedBytes ────────────────

    [Fact]
    public void ReadUInt32WithConsumedBytes_PositiveValues_RoundTrip()
    {
        var values = new int[] { 0, 1, 100, 255, 65535 };
        var writer = new BufferWriter(DefaultParquetBufferPool.Shared, 4096, 4096);
        DeltaBinaryPackedEncoding.WriteInt32(values, ref writer);
        var payload = new byte[writer.WrittenLength];
        writer.CopyTo(payload);

        var (result, consumed) = DeltaBinaryPackedDecoder.ReadUInt32WithConsumedBytes(payload);
        Assert.Equal((uint[])([0, 1, 100, 255, 65535]), result);
        Assert.Equal(writer.WrittenLength, consumed);
    }

    [Fact]
    public void ReadUInt32WithConsumedBytes_NegativeValue_Throws()
    {
        var values = new int[] { 1, -1, 3 }; // -1 encoded and decoded as -1, which is < 0
        var writer = new BufferWriter(DefaultParquetBufferPool.Shared, 4096, 4096);
        DeltaBinaryPackedEncoding.WriteInt32(values, ref writer);
        var payload = new byte[writer.WrittenLength];
        writer.CopyTo(payload);

        Assert.Throws<CorruptParquetException>(() =>
            DeltaBinaryPackedDecoder.ReadUInt32WithConsumedBytes(payload));
    }

    // ──────────────── Edge cases that target surviving conditional mutations ────────────────

    [Fact]
    public void Int32_TwoValues_ExactDiff()
    {
        // Tests the delta encoding/decoding path with just 2 values
        var result = EncodeDecodeInt32([100, 200]);
        Assert.Equal([100, 200], result);
    }

    [Fact]
    public void Int32_LargeWideDeltas_RoundTrip()
    {
        // Forces wide bitwidth (large deltas between consecutive values)
        var values = new int[] { 0, int.MaxValue / 2, 0, -(int.MaxValue / 2), 0 };
        Assert.Equal(values, EncodeDecodeInt32(values));
    }

    [Fact]
    public void Int32_AlternatingMinMax_RoundTrip()
    {
        // Values: int.MinValue, int.MaxValue, int.MinValue, int.MaxValue
        var values = new int[] { int.MinValue, int.MaxValue, int.MinValue, int.MaxValue };
        Assert.Equal(values, EncodeDecodeInt32(values));
    }

    [Fact]
    public void Int64_LargeValues_RoundTrip()
    {
        // Large int64 timestamps
        var values = new long[] { 1_000_000_000_000L, 1_000_001_000_000L, 1_000_002_000_000L };
        Assert.Equal(values, EncodeDecodeInt64(values));
    }

    [Fact]
    public void Int32_ExactlyTwoBlocks_RoundTrip()
    {
        // 256 values = exactly 2 blocks of 128
        var values = Enumerable.Range(0, 256).ToArray();
        Assert.Equal(values, EncodeDecodeInt32(values));
    }

    [Fact]
    public void Int32_OneMoreThanBlock_RoundTrip()
    {
        // 129 values = 1 full block + 1 partial
        var values = Enumerable.Range(10, 129).ToArray();
        Assert.Equal(values, EncodeDecodeInt32(values));
    }

    [Fact]
    public void Int64_EmptyIntoDestination_Succeeds()
    {
        var writer = new BufferWriter(DefaultParquetBufferPool.Shared, 4096, 4096);
        DeltaBinaryPackedEncoding.WriteInt64([], ref writer);
        var payload = new byte[writer.WrittenLength];
        writer.CopyTo(payload);
        var consumedBytes = DeltaBinaryPackedDecoder.ReadInt64(payload, Span<long>.Empty);
        Assert.True(consumedBytes > 0);
    }
}
