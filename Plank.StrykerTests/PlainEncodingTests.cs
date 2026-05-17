using System.Collections.Immutable;
using Plank.Schema;
using Plank.Writing;
using Plank.Writing.Encoding;

namespace Plank.StrykerTests;

public class PlainEncodingTests
{
    static Column BoolCol() => new("v", ParquetPhysicalType.Boolean);
    static Column Int32Col() => new("v", ParquetPhysicalType.Int32);
    static Column Int64Col() => new("v", ParquetPhysicalType.Int64);
    static Column FloatCol() => new("v", ParquetPhysicalType.Float);
    static Column DoubleCol() => new("v", ParquetPhysicalType.Double);

    static byte[] WritePlain<T>(Column column, T[] values) where T : notnull
    {
        var writer = new BufferWriter(DefaultParquetBufferPool.Shared, 4096, 4096);
        PlainEncoding.WriteValues(column, values.AsSpan(), ref writer);
        var result = new byte[writer.WrittenLength];
        writer.CopyTo(result);
        return result;
    }

    // ──────────────── Boolean bit packing (lines 59, 65) ────────────────

    [Fact]
    public void Boolean_ZeroValues_ProducesZeroBytes()
    {
        var encoded = WritePlain(BoolCol(), new bool[0]);
        Assert.Equal(0, encoded.Length);
    }

    [Fact]
    public void Boolean_OneValue_ProducesOneByte()
    {
        var encoded = WritePlain(BoolCol(), new bool[] { true });
        Assert.Equal(1, encoded.Length);
        Assert.Equal(1, encoded[0] & 1); // bit 0 = true
    }

    [Fact]
    public void Boolean_EightValues_ProducesOneByte()
    {
        var encoded = WritePlain(BoolCol(), new bool[] { true, false, true, false, true, false, true, false });
        Assert.Equal(1, encoded.Length); // (8+7)>>3 = 1
        // bit 0=1, bit 1=0, bit 2=1, bit 3=0 → byte = 0b01010101 = 0x55
        Assert.Equal(0x55, encoded[0]);
    }

    [Fact]
    public void Boolean_NineValues_ProducesTwoBytes()
    {
        var values = new bool[9];
        values[8] = true; // last bit
        var encoded = WritePlain(BoolCol(), values);
        Assert.Equal(2, encoded.Length); // (9+7)>>3 = 2
    }

    [Fact]
    public void Boolean_SixteenValues_ProducesTwoBytes()
    {
        var encoded = WritePlain(BoolCol(), new bool[16]);
        Assert.Equal(2, encoded.Length); // (16+7)>>3 = 2
    }

    [Fact]
    public void Boolean_SeventeenValues_ProducesThreeBytes()
    {
        var encoded = WritePlain(BoolCol(), new bool[17]);
        Assert.Equal(3, encoded.Length); // (17+7)>>3 = 3
    }

    [Fact]
    public void Boolean_AllTrue_AllBitsSet()
    {
        // 8 true values → all bits set in one byte
        var encoded = WritePlain(BoolCol(), new bool[] { true, true, true, true, true, true, true, true });
        Assert.Equal(1, encoded.Length);
        Assert.Equal(0xFF, encoded[0]);
    }

    [Fact]
    public void Boolean_AllFalse_NoBitsSet()
    {
        var encoded = WritePlain(BoolCol(), new bool[] { false, false, false, false, false, false, false, false });
        Assert.Equal(1, encoded.Length);
        Assert.Equal(0x00, encoded[0]);
    }

    [Fact]
    public void Boolean_LargeArray_CorrectByteCount()
    {
        // 100 booleans → (100+7)/8 = 13 bytes
        var encoded = WritePlain(BoolCol(), new bool[100]);
        Assert.Equal(13, encoded.Length);
    }

    [Fact]
    public void Boolean_256Values_Exactly32Bytes()
    {
        var encoded = WritePlain(BoolCol(), new bool[256]);
        Assert.Equal(32, encoded.Length); // 256/8 = 32
    }

    [Fact]
    public void Boolean_BitOrdering_LsbFirst()
    {
        // true, false, false, false, false, false, false, false → bit 0 = 1 → byte = 0x01
        var encoded = WritePlain(BoolCol(), new bool[] { true, false, false, false, false, false, false, false });
        Assert.Equal(0x01, encoded[0]);
    }

    [Fact]
    public void Boolean_SecondBit()
    {
        // false, true, ... → bit 1 = 1 → byte = 0x02
        var encoded = WritePlain(BoolCol(), new bool[] { false, true, false, false, false, false, false, false });
        Assert.Equal(0x02, encoded[0]);
    }

    // ──────────────── Int32 encoding ────────────────

    [Fact]
    public void Int32_ZeroValues_ProducesZeroBytes()
    {
        var encoded = WritePlain(Int32Col(), new int[0]);
        Assert.Equal(0, encoded.Length);
    }

    [Fact]
    public void Int32_OneValue_ProducesFourBytes()
    {
        var encoded = WritePlain(Int32Col(), new int[] { 42 });
        Assert.Equal(4, encoded.Length);
        // Little-endian 42
        Assert.Equal(42, BitConverter.ToInt32(encoded, 0));
    }

    [Fact]
    public void Int32_MultipleValues_CorrectSize()
    {
        var encoded = WritePlain(Int32Col(), new int[] { 1, 2, 3, 4, 5 });
        Assert.Equal(20, encoded.Length); // 5 * 4 bytes
        for (var i = 0; i < 5; i++)
            Assert.Equal(i + 1, BitConverter.ToInt32(encoded, i * 4));
    }

    [Fact]
    public void Int32_NegativeValue_LittleEndianEncoded()
    {
        var encoded = WritePlain(Int32Col(), new int[] { -1 });
        Assert.Equal(4, encoded.Length);
        Assert.Equal(-1, BitConverter.ToInt32(encoded, 0));
    }

    // ──────────────── Int64 encoding ────────────────

    [Fact]
    public void Int64_OneValue_ProducesEightBytes()
    {
        var encoded = WritePlain(Int64Col(), new long[] { 1_000_000_000_000L });
        Assert.Equal(8, encoded.Length);
        Assert.Equal(1_000_000_000_000L, BitConverter.ToInt64(encoded, 0));
    }

    [Fact]
    public void Int64_MultipleValues_CorrectSize()
    {
        var values = new long[] { 1L, 2L, 3L };
        var encoded = WritePlain(Int64Col(), values);
        Assert.Equal(24, encoded.Length); // 3 * 8
        for (var i = 0; i < 3; i++)
            Assert.Equal(values[i], BitConverter.ToInt64(encoded, i * 8));
    }

    // ──────────────── Float encoding ────────────────

    [Fact]
    public void Float_OneValue_ProducesFourBytes()
    {
        var encoded = WritePlain(FloatCol(), new float[] { 1.5f });
        Assert.Equal(4, encoded.Length);
        Assert.Equal(1.5f, BitConverter.ToSingle(encoded, 0));
    }

    // ──────────────── Double encoding ────────────────

    [Fact]
    public void Double_OneValue_ProducesEightBytes()
    {
        var encoded = WritePlain(DoubleCol(), new double[] { 3.14 });
        Assert.Equal(8, encoded.Length);
        Assert.Equal(3.14, BitConverter.ToDouble(encoded, 0));
    }

    // ──────────────── Via roundtrip — exercises NoCoverage ────────────────

    [Fact]
    public void Boolean_Via_Roundtrip_LargeDataset()
    {
        // Forces the SIMD paths with large boolean arrays
        var schema = new ParquetSchema([new Column("v", ParquetPhysicalType.Boolean)]);
        var values = Enumerable.Range(0, 512).Select(i => i % 3 != 0).ToArray();
        using var ms = new MemoryStream();
        var writer = schema.CreateWriter(ms, new ParquetWriterOptions { Compression = CompressionKind.None });
        var col = writer.CreateSerializedColumn<bool>(schema.Columns[0]);
        col.Serialize(values);
        writer.StartRowGroup().Write(col);
        writer.CloseFile();

        var src = new Plank.Reading.MemoryReadSource(ms.ToArray());
        using var reader = schema.CreateReader(src);
        var result = new List<bool>();
        foreach (var tok in reader.EnumerateRowGroups())
        {
            using var rg = reader.OpenRowGroup(src, tok);
            foreach (var page in rg.Column<bool>(schema.Columns[0]).Pages)
                foreach (var v in page.Values.Span)
                    result.Add(v);
        }
        Assert.Equal(values, result.ToArray());
    }

    [Fact]
    public void Boolean_Via_Roundtrip_SizesAroundBoundaries()
    {
        var schema = new ParquetSchema([new Column("v", ParquetPhysicalType.Boolean)]);
        // Test sizes around 8, 16, 32 (byte boundaries)
        foreach (var count in new[] { 7, 8, 9, 15, 16, 17, 31, 32, 33 })
        {
            var values = Enumerable.Range(0, count).Select(i => i % 2 == 0).ToArray();
            using var ms = new MemoryStream();
            var writer = schema.CreateWriter(ms, new ParquetWriterOptions { Compression = CompressionKind.None });
            var col = writer.CreateSerializedColumn<bool>(schema.Columns[0]);
            col.Serialize(values);
            writer.StartRowGroup().Write(col);
            writer.CloseFile();

            var src = new Plank.Reading.MemoryReadSource(ms.ToArray());
            using var reader = schema.CreateReader(src);
            var result = new List<bool>();
            foreach (var tok in reader.EnumerateRowGroups())
            {
                using var rg = reader.OpenRowGroup(src, tok);
                foreach (var page in rg.Column<bool>(schema.Columns[0]).Pages)
                    foreach (var v in page.Values.Span)
                        result.Add(v);
            }
            Assert.Equal(values, result.ToArray());
        }
    }
}
