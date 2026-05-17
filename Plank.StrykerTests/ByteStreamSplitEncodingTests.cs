using Plank.Schema;
using Plank.Writing;
using Plank.Writing.Encoding;

namespace Plank.StrykerTests;

/// <summary>
/// Direct unit tests for ByteStreamSplitEncoding.WriteValues targeting:
/// - WriteInt32Lanes for byte, ushort, uint types (lines 202-241) — NoCoverage
/// - WriteInt64Lanes for ulong type (lines 278-293) — NoCoverage
/// - WriteFixedLengthByteArrayValues (lines 134-165) — NoCoverage
/// - Surviving mutants in byte count checks and lane write arithmetic
/// </summary>
public class ByteStreamSplitEncodingTests
{
    static Column Int32Col(string name = "v") => new(name, ParquetPhysicalType.Int32,
        new ColumnOptions(encodings: [EncodingKind.ByteStreamSplit]));
    static Column Int64Col(string name = "v") => new(name, ParquetPhysicalType.Int64,
        new ColumnOptions(encodings: [EncodingKind.ByteStreamSplit]));
    static Column FloatCol(string name = "v") => new(name, ParquetPhysicalType.Float,
        new ColumnOptions(encodings: [EncodingKind.ByteStreamSplit]));
    static Column DoubleCol(string name = "v") => new(name, ParquetPhysicalType.Double,
        new ColumnOptions(encodings: [EncodingKind.ByteStreamSplit]));

    static byte[] Write<T>(Column col, T[] values) where T : notnull
    {
        var writer = new BufferWriter(DefaultParquetBufferPool.Shared, 4096, 4096);
        ByteStreamSplitEncoding.WriteValues(col, values.AsSpan(), ref writer);
        var result = new byte[writer.WrittenLength];
        writer.CopyTo(result);
        return result;
    }

    // ──────────────── WriteInt32Lanes<int> — byte count and layout ────────────────

    [Test]
    public void WriteInt32_Empty_ProducesNoOutput()
    {
        var encoded = Write(Int32Col(), new int[0]);
        ClassicAssert.AreEqual(0, encoded.Length);
    }

    [Test]
    public void WriteInt32_SingleValue_FourBytesInLanes()
    {
        // 0x01020304 stored in 4 lanes: lane0=04, lane1=03, lane2=02, lane3=01
        var encoded = Write(Int32Col(), new int[] { 0x01020304 });
        ClassicAssert.AreEqual(4, encoded.Length);
        ClassicAssert.AreEqual(0x04, encoded[0]); // lane0 (LSB)
        ClassicAssert.AreEqual(0x03, encoded[1]); // lane1
        ClassicAssert.AreEqual(0x02, encoded[2]); // lane2
        ClassicAssert.AreEqual(0x01, encoded[3]); // lane3 (MSB)
    }

    [Test]
    public void WriteInt32_TwoValues_LaneInterleaved()
    {
        // values[0]=0x01020304, values[1]=0x05060708
        // lane0: [04, 08], lane1: [03, 07], lane2: [02, 06], lane3: [01, 05]
        var encoded = Write(Int32Col(), new int[] { 0x01020304, 0x05060708 });
        ClassicAssert.AreEqual(8, encoded.Length);
        ClassicAssert.AreEqual(0x04, encoded[0]); // lane0[0]
        ClassicAssert.AreEqual(0x08, encoded[1]); // lane0[1]
        ClassicAssert.AreEqual(0x03, encoded[2]); // lane1[0]
        ClassicAssert.AreEqual(0x07, encoded[3]); // lane1[1]
        ClassicAssert.AreEqual(0x02, encoded[4]); // lane2[0]
        ClassicAssert.AreEqual(0x06, encoded[5]); // lane2[1]
        ClassicAssert.AreEqual(0x01, encoded[6]); // lane3[0]
        ClassicAssert.AreEqual(0x05, encoded[7]); // lane3[1]
    }

    [Test]
    public void WriteInt32_Zero_AllBytesZero()
    {
        var encoded = Write(Int32Col(), new int[] { 0 });
        ClassicAssert.AreEqual(4, encoded.Length);
        Assert.That(encoded, Is.All.EqualTo(0));
    }

    [Test]
    public void WriteInt32_AllFF_AllBytesFF()
    {
        var encoded = Write(Int32Col(), new int[] { -1 }); // 0xFFFFFFFF
        ClassicAssert.AreEqual(4, encoded.Length);
        Assert.That(encoded, Is.All.EqualTo((byte)0xFF));
    }

    // ──────────────── WriteInt32Lanes<byte> (line 202-213) ────────────────

    [Test]
    public void WriteInt32_ByteValues_LowByteInLane0_HighLanesZero()
    {
        // byte value 0x42 → stored in Int32 lane: lane0=0x42, lanes 1-3 = 0x00
        var col = new Column("v", ParquetPhysicalType.Int32,
            new ColumnOptions(encodings: [EncodingKind.ByteStreamSplit]));
        var encoded = Write(col, new byte[] { 0x42, 0xFF });
        ClassicAssert.AreEqual(8, encoded.Length); // 2 values × 4 bytes
        // lane0: [0x42, 0xFF], lanes 1-3: [0x00, 0x00]
        ClassicAssert.AreEqual(0x42, encoded[0]); // lane0[0]
        ClassicAssert.AreEqual(0xFF, encoded[1]); // lane0[1]
        ClassicAssert.AreEqual(0x00, encoded[2]); // lane1[0]
        ClassicAssert.AreEqual(0x00, encoded[3]); // lane1[1]
        ClassicAssert.AreEqual(0x00, encoded[4]); // lane2[0]
        ClassicAssert.AreEqual(0x00, encoded[5]); // lane2[1]
        ClassicAssert.AreEqual(0x00, encoded[6]); // lane3[0]
        ClassicAssert.AreEqual(0x00, encoded[7]); // lane3[1]
    }

    [Test]
    public void WriteInt32_ByteValues_Single()
    {
        var col = new Column("v", ParquetPhysicalType.Int32,
            new ColumnOptions(encodings: [EncodingKind.ByteStreamSplit]));
        var encoded = Write(col, new byte[] { 200 });
        ClassicAssert.AreEqual(4, encoded.Length);
        ClassicAssert.AreEqual(200, encoded[0]);   // lane0 = byte value
        ClassicAssert.AreEqual(0, encoded[1]);     // lane1 = 0 (high bits)
        ClassicAssert.AreEqual(0, encoded[2]);
        ClassicAssert.AreEqual(0, encoded[3]);
    }

    // ──────────────── WriteInt32Lanes<ushort> (line 216-227) ────────────────

    [Test]
    public void WriteInt32_UShortValues_TwoLanesUsed()
    {
        // ushort 0x0102 → lane0=0x02, lane1=0x01, lanes 2-3 = 0x00
        var col = new Column("v", ParquetPhysicalType.Int32,
            new ColumnOptions(encodings: [EncodingKind.ByteStreamSplit]));
        var encoded = Write(col, new ushort[] { 0x0102, 0x0304 });
        ClassicAssert.AreEqual(8, encoded.Length);
        // lane0: [0x02, 0x04]
        ClassicAssert.AreEqual(0x02, encoded[0]);
        ClassicAssert.AreEqual(0x04, encoded[1]);
        // lane1: [0x01, 0x03]
        ClassicAssert.AreEqual(0x01, encoded[2]);
        ClassicAssert.AreEqual(0x03, encoded[3]);
        // lanes 2-3: all zeros
        ClassicAssert.AreEqual(0, encoded[4]);
        ClassicAssert.AreEqual(0, encoded[5]);
        ClassicAssert.AreEqual(0, encoded[6]);
        ClassicAssert.AreEqual(0, encoded[7]);
    }

    [Test]
    public void WriteInt32_UShortMax_CorrectLanes()
    {
        var col = new Column("v", ParquetPhysicalType.Int32,
            new ColumnOptions(encodings: [EncodingKind.ByteStreamSplit]));
        var encoded = Write(col, new ushort[] { 65535 }); // 0xFFFF
        ClassicAssert.AreEqual(4, encoded.Length);
        ClassicAssert.AreEqual(0xFF, encoded[0]); // lane0
        ClassicAssert.AreEqual(0xFF, encoded[1]); // lane1
        ClassicAssert.AreEqual(0x00, encoded[2]); // lane2 = 0 (ushort has no high 16 bits)
        ClassicAssert.AreEqual(0x00, encoded[3]); // lane3 = 0
    }

    // ──────────────── WriteInt32Lanes<uint> (line 230-241) ────────────────

    [Test]
    public void WriteInt32_UIntValues_AllFourLanesUsed()
    {
        var col = new Column("v", ParquetPhysicalType.Int32,
            new ColumnOptions(encodings: [EncodingKind.ByteStreamSplit]));
        var encoded = Write(col, new uint[] { 0x01020304u, 0x05060708u });
        ClassicAssert.AreEqual(8, encoded.Length);
        ClassicAssert.AreEqual(0x04, encoded[0]); // lane0[0]
        ClassicAssert.AreEqual(0x08, encoded[1]); // lane0[1]
        ClassicAssert.AreEqual(0x03, encoded[2]); // lane1[0]
        ClassicAssert.AreEqual(0x07, encoded[3]); // lane1[1]
        ClassicAssert.AreEqual(0x02, encoded[4]); // lane2[0]
        ClassicAssert.AreEqual(0x06, encoded[5]); // lane2[1]
        ClassicAssert.AreEqual(0x01, encoded[6]); // lane3[0]
        ClassicAssert.AreEqual(0x05, encoded[7]); // lane3[1]
    }

    [Test]
    public void WriteInt32_UIntMax_AllFF()
    {
        var col = new Column("v", ParquetPhysicalType.Int32,
            new ColumnOptions(encodings: [EncodingKind.ByteStreamSplit]));
        var encoded = Write(col, new uint[] { uint.MaxValue });
        ClassicAssert.AreEqual(4, encoded.Length);
        Assert.That(encoded, Is.All.EqualTo((byte)0xFF));
    }

    // ──────────────── WriteInt64Lanes<long> ────────────────

    [Test]
    public void WriteInt64_SingleValue_EightBytesInLanes()
    {
        var encoded = Write(Int64Col(), new long[] { 0x0102030405060708L });
        ClassicAssert.AreEqual(8, encoded.Length);
        ClassicAssert.AreEqual(0x08, encoded[0]); // lane0 (LSB)
        ClassicAssert.AreEqual(0x07, encoded[1]); // lane1
        ClassicAssert.AreEqual(0x06, encoded[2]); // lane2
        ClassicAssert.AreEqual(0x05, encoded[3]); // lane3
        ClassicAssert.AreEqual(0x04, encoded[4]); // lane4
        ClassicAssert.AreEqual(0x03, encoded[5]); // lane5
        ClassicAssert.AreEqual(0x02, encoded[6]); // lane6
        ClassicAssert.AreEqual(0x01, encoded[7]); // lane7 (MSB)
    }

    [Test]
    public void WriteInt64_TwoValues_LaneInterleaved()
    {
        var encoded = Write(Int64Col(), new long[] { 0x0102030405060708L, 0x090A0B0C0D0E0F10L });
        ClassicAssert.AreEqual(16, encoded.Length);
        // lane0: [0x08, 0x10]
        ClassicAssert.AreEqual(0x08, encoded[0]);
        ClassicAssert.AreEqual(0x10, encoded[1]);
        // lane7: [0x01, 0x09]
        ClassicAssert.AreEqual(0x01, encoded[14]);
        ClassicAssert.AreEqual(0x09, encoded[15]);
    }

    // ──────────────── WriteInt64Lanes<ulong> (line 278-293) ────────────────

    [Test]
    public void WriteInt64_ULongValues_EightLanes()
    {
        var col = new Column("v", ParquetPhysicalType.Int64,
            new ColumnOptions(encodings: [EncodingKind.ByteStreamSplit]));
        var encoded = Write(col, new ulong[] { 0x0102030405060708UL });
        ClassicAssert.AreEqual(8, encoded.Length);
        ClassicAssert.AreEqual(0x08, encoded[0]); // lane0
        ClassicAssert.AreEqual(0x07, encoded[1]); // lane1
        ClassicAssert.AreEqual(0x01, encoded[7]); // lane7
    }

    [Test]
    public void WriteInt64_ULongMax_AllFF()
    {
        var col = new Column("v", ParquetPhysicalType.Int64,
            new ColumnOptions(encodings: [EncodingKind.ByteStreamSplit]));
        var encoded = Write(col, new ulong[] { ulong.MaxValue });
        ClassicAssert.AreEqual(8, encoded.Length);
        Assert.That(encoded, Is.All.EqualTo((byte)0xFF));
    }

    // ──────────────── WriteFloatValues — byte count (lines 69-89) ────────────────

    [Test]
    public void WriteFloat_Zero_FourBytesAllZero()
    {
        var encoded = Write(FloatCol(), new float[] { 0.0f });
        ClassicAssert.AreEqual(4, encoded.Length);
        Assert.That(encoded, Is.All.EqualTo(0));
    }

    [Test]
    public void WriteFloat_TwoValues_EightBytes()
    {
        var encoded = Write(FloatCol(), new float[] { 1.0f, -1.0f });
        ClassicAssert.AreEqual(8, encoded.Length); // 2 × 4 bytes
    }

    // ──────────────── WriteDoubleValues — byte count (lines 100-131) ────────────────

    [Test]
    public void WriteDouble_SingleValue_EightBytes()
    {
        var encoded = Write(DoubleCol(), new double[] { 1.0 });
        ClassicAssert.AreEqual(8, encoded.Length);
    }

    [Test]
    public void WriteDouble_TwoValues_SixteenBytes()
    {
        var encoded = Write(DoubleCol(), new double[] { 1.0, 2.0 });
        ClassicAssert.AreEqual(16, encoded.Length); // 2 × 8 bytes
    }

    // ──────────────── WriteFixedLengthByteArrayValues (lines 134-165) ────────────────

    [Test]
    public void WriteFixedLen_SingleValue_LaneInterleaved()
    {
        var col = new Column("v", ParquetPhysicalType.FixedLenByteArray,
            new ColumnOptions(encodings: [EncodingKind.ByteStreamSplit], typeLength: 4));
        // [0x01, 0x02, 0x03, 0x04] stored in 4 lanes:
        // lane0 (byte[0]): 0x01, lane1 (byte[1]): 0x02, lane2: 0x03, lane3: 0x04
        var encoded = Write(col, new byte[][] { new byte[] { 0x01, 0x02, 0x03, 0x04 } });
        ClassicAssert.AreEqual(4, encoded.Length);
        ClassicAssert.AreEqual(0x01, encoded[0]); // lane0
        ClassicAssert.AreEqual(0x02, encoded[1]); // lane1
        ClassicAssert.AreEqual(0x03, encoded[2]); // lane2
        ClassicAssert.AreEqual(0x04, encoded[3]); // lane3
    }

    [Test]
    public void WriteFixedLen_TwoValues_LaneInterleaved()
    {
        var col = new Column("v", ParquetPhysicalType.FixedLenByteArray,
            new ColumnOptions(encodings: [EncodingKind.ByteStreamSplit], typeLength: 2));
        var encoded = Write(col, new byte[][] {
            new byte[] { 0x01, 0x02 },
            new byte[] { 0x03, 0x04 }
        });
        ClassicAssert.AreEqual(4, encoded.Length); // 2 values × 2 bytes
        // lane0 (byte[0]): [0x01, 0x03]
        ClassicAssert.AreEqual(0x01, encoded[0]);
        ClassicAssert.AreEqual(0x03, encoded[1]);
        // lane1 (byte[1]): [0x02, 0x04]
        ClassicAssert.AreEqual(0x02, encoded[2]);
        ClassicAssert.AreEqual(0x04, encoded[3]);
    }

    [Test]
    public void WriteFixedLen_TypeLengthZero_Throws()
    {
        var col = new Column("v", ParquetPhysicalType.FixedLenByteArray,
            new ColumnOptions(encodings: [EncodingKind.ByteStreamSplit])); // typeLength defaults to 0
        try { Write(col, new byte[][] { new byte[] { 1, 2 } }); Assert.Fail("Expected exception"); }
        catch (InvalidOperationException) { }
    }

}
