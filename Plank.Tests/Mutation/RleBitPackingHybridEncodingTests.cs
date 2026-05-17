using Plank.Writing;
using Plank.Writing.Encoding;

namespace Plank.Tests.Mutation;

public class RleBitPackingHybridEncodingTests
{
    static byte[] Encode(int[] values, int bitWidth)
    {
        var writer = new BufferWriter(DefaultParquetBufferPool.Shared, 4096, 4096);
        RleBitPackingHybridEncoding.Write(values, bitWidth, ref writer);
        var result = new byte[writer.WrittenLength];
        writer.CopyTo(result);
        return result;
    }

    static byte[] EncodeWithPrefix(int[] values, int bitWidth)
    {
        var writer = new BufferWriter(DefaultParquetBufferPool.Shared, 4096, 4096);
        RleBitPackingHybridEncoding.WriteWithBitWidthPrefix(values, bitWidth, ref writer);
        var result = new byte[writer.WrittenLength];
        writer.CopyTo(result);
        return result;
    }

    static byte[] EncodeBooleans(bool[] values)
    {
        var writer = new BufferWriter(DefaultParquetBufferPool.Shared, 4096, 4096);
        RleBitPackingHybridEncoding.WriteBooleans(values, ref writer);
        var result = new byte[writer.WrittenLength];
        writer.CopyTo(result);
        return result;
    }

    // ──────────────── GetBitWidthFromMaxValue ────────────────

    [Test]
    public void GetBitWidth_Zero_Returns0()
        => ClassicAssert.AreEqual(0, RleBitPackingHybridEncoding.GetBitWidthFromMaxValue(0));

    [Test]
    public void GetBitWidth_One_Returns1()
        => ClassicAssert.AreEqual(1, RleBitPackingHybridEncoding.GetBitWidthFromMaxValue(1));

    [Test]
    public void GetBitWidth_Two_Returns2()
        => ClassicAssert.AreEqual(2, RleBitPackingHybridEncoding.GetBitWidthFromMaxValue(2));

    [Test]
    public void GetBitWidth_Three_Returns2()
        => ClassicAssert.AreEqual(2, RleBitPackingHybridEncoding.GetBitWidthFromMaxValue(3));

    [Test]
    public void GetBitWidth_Four_Returns3()
        => ClassicAssert.AreEqual(3, RleBitPackingHybridEncoding.GetBitWidthFromMaxValue(4));

    [Test]
    public void GetBitWidth_15_Returns4()
        => ClassicAssert.AreEqual(4, RleBitPackingHybridEncoding.GetBitWidthFromMaxValue(15));

    [Test]
    public void GetBitWidth_16_Returns5()
        => ClassicAssert.AreEqual(5, RleBitPackingHybridEncoding.GetBitWidthFromMaxValue(16));

    [Test]
    public void GetBitWidth_255_Returns8()
        => ClassicAssert.AreEqual(8, RleBitPackingHybridEncoding.GetBitWidthFromMaxValue(255));

    [Test]
    public void GetBitWidth_256_Returns9()
        => ClassicAssert.AreEqual(9, RleBitPackingHybridEncoding.GetBitWidthFromMaxValue(256));

    [Test]
    public void GetBitWidth_IntMax_Returns31()
        => ClassicAssert.AreEqual(31, RleBitPackingHybridEncoding.GetBitWidthFromMaxValue(int.MaxValue));

    [Test]
    public void GetBitWidth_Negative_Throws()
        => Assert.Throws<ArgumentOutOfRangeException>(() =>
            RleBitPackingHybridEncoding.GetBitWidthFromMaxValue(-1));

    // ──────────────── Write produces non-empty output ────────────────

    [Test]
    public void Write_Empty_ProducesNoOutput()
    {
        var encoded = Encode([], bitWidth: 4);
        ClassicAssert.AreEqual(0, encoded.Length);
    }

    [Test]
    public void Write_ZeroBitWidth_AllSameValue_Compact()
    {
        // bit width 0 → all values are 0, RLE only
        var encoded = Encode([0, 0, 0, 0, 0, 0, 0, 0], bitWidth: 0);
        ClassicAssert.IsTrue(encoded.Length > 0);
    }

    [Test]
    public void Write_OneBit_BooleanLike()
    {
        // bit width 1 → encodes 0/1 values
        var values = new int[] { 0, 1, 0, 0, 1, 1, 0, 1 };
        var encoded = Encode(values, bitWidth: 1);
        ClassicAssert.IsTrue(encoded.Length > 0);
    }

    [Test]
    public void Write_RunRle_SameValueRepeated()
    {
        // Long run of same value → RLE encoding
        var values = Enumerable.Repeat(5, 100).ToArray();
        var encoded = Encode(values, bitWidth: 4);
        ClassicAssert.IsTrue(encoded.Length > 0);
        // RLE should be compact: 1 run header + value bytes
        ClassicAssert.IsTrue(encoded.Length < 50); // much less than 100 * 1 byte
    }

    [Test]
    public void WriteWithPrefix_ProducesLargerOutput()
    {
        // WriteWithBitWidthPrefix adds 1 byte prefix
        var values = new int[] { 1, 2, 3 };
        var withoutPrefix = Encode(values, bitWidth: 2);
        var withPrefix = EncodeWithPrefix(values, bitWidth: 2);
        ClassicAssert.IsTrue(withPrefix.Length > withoutPrefix.Length);
    }

    [Test]
    public void WriteBooleans_Empty_ProducesNoOutput()
    {
        var encoded = EncodeBooleans([]);
        ClassicAssert.AreEqual(0, encoded.Length);
    }

    [Test]
    public void WriteBooleans_AllTrue_ProducesOutput()
    {
        var encoded = EncodeBooleans([true, true, true, true]);
        ClassicAssert.IsTrue(encoded.Length > 0);
    }

    [Test]
    public void WriteBooleans_AllFalse_ProducesOutput()
    {
        var encoded = EncodeBooleans([false, false, false, false]);
        ClassicAssert.IsTrue(encoded.Length > 0);
    }

    [Test]
    public void WriteBooleans_Mixed_ProducesOutput()
    {
        var encoded = EncodeBooleans([true, false, true, false, true]);
        ClassicAssert.IsTrue(encoded.Length > 0);
    }

    [Test]
    public void WriteBooleans_LongRun_CompactOutput()
    {
        // 100 same booleans → RLE
        var encoded = EncodeBooleans(Enumerable.Repeat(true, 100).ToArray());
        ClassicAssert.IsTrue(encoded.Length < 20); // RLE should be very compact
    }

    // ──────────────── Via dictionary encoding roundtrip (exercises RleBitPacking) ────────────────

    [Test]
    public void Write_FourBits_LiteralsEncoded()
    {
        // Values 0-9 need 4 bits; diverse values → bit-packed
        var values = Enumerable.Range(0, 10).ToArray();
        var encoded = Encode(values, bitWidth: 4);
        ClassicAssert.IsTrue(encoded.Length > 0);
    }

    [Test]
    public void WriteUnchecked_SameAsWrite_ForSmallValues()
    {
        var values = new int[] { 0, 1, 2, 3, 0, 1, 2, 3 };
        var normalWriter = new BufferWriter(DefaultParquetBufferPool.Shared, 4096, 4096);
        var uncheckedWriter = new BufferWriter(DefaultParquetBufferPool.Shared, 4096, 4096);
        RleBitPackingHybridEncoding.Write(values, 2, ref normalWriter);
        RleBitPackingHybridEncoding.WriteUnchecked(values, 2, ref uncheckedWriter);
        var normal = new byte[normalWriter.WrittenLength];
        var unchecked_ = new byte[uncheckedWriter.WrittenLength];
        normalWriter.CopyTo(normal);
        uncheckedWriter.CopyTo(unchecked_);
        ClassicAssert.AreEqual(normal, unchecked_);
    }
}
