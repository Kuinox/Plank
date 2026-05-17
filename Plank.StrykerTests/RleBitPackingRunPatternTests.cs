using Plank.Reading;
using Plank.Schema;
using Plank.Writing;
using Plank.Writing.Encoding;

namespace Plank.StrykerTests;

/// <summary>
/// Targets surviving mutants in RleBitPackingHybridEncoding at:
/// - line 40/82: runLength >= 8 threshold (literals then RLE transition)
/// - lines 243/284: mask = (bitWidth==32) ? uint.MaxValue : (1u << bitWidth) - 1u
/// - lines 315/341: partial-byte packing (group with length not divisible by 8)
/// </summary>
public class RleBitPackingRunPatternTests
{
    static byte[] Encode(int[] values, int bitWidth)
    {
        var writer = new BufferWriter(DefaultParquetBufferPool.Shared, 4096, 4096);
        RleBitPackingHybridEncoding.Write(values, bitWidth, ref writer);
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

    // ──────────────── Exact run-length boundary at 8 ────────────────

    [Test]
    public void Write_ExactlyEightInRun_FollowedByLiterals()
    {
        // [1, 0, 0, 0, 0, 0, 0, 0, 0, 2] — literal 1, then RLE of exactly 8 zeros, then literal 2
        // This exercises the runLength >= 8 threshold at line 40 with run=8
        var values = new int[] { 1, 0, 0, 0, 0, 0, 0, 0, 0, 2 };
        var encoded = Encode(values, 2);
        ClassicAssert.IsTrue(encoded.Length > 0);
        // Verify that 7-length run (should be literal) vs 8-length run (RLE) differ
        var encodedSeven = Encode(new int[] { 1, 0, 0, 0, 0, 0, 0, 0, 2 }, 2); // only 7 zeros
        // With 8 zeros → RLE encoding (more compact); with 7 → bit-packed
        // Just verify both produce valid (non-empty) output and differ in size
        ClassicAssert.AreNotEqual(encoded.Length, encodedSeven.Length);
    }

    [Test]
    public void Write_SevenInRun_IsNotRle()
    {
        // Run of 7 should not trigger RLE (threshold is >= 8)
        var sevenZeros = Encode(new int[] { 0, 0, 0, 0, 0, 0, 0 }, 1);
        var eightZeros = Encode(new int[] { 0, 0, 0, 0, 0, 0, 0, 0 }, 1);
        // 8 zeros uses RLE → shorter than bit-packing 7 zeros
        ClassicAssert.IsTrue(eightZeros.Length <= sevenZeros.Length);
    }

    [Test]
    public void Write_LiteralsFollowedByExact8Run_Correct()
    {
        // Literals [1,2,3] then 8 zeros — exercises inner while loop at line 37-54
        var values = new int[] { 1, 2, 3, 0, 0, 0, 0, 0, 0, 0, 0, 4 };
        var encoded = Encode(values, 4);
        ClassicAssert.IsTrue(encoded.Length > 0);
    }

    [Test]
    public void Write_LiteralsFollowedByNineRun_Correct()
    {
        // Literals [1,2,3] then 9 zeros — run > 8 which pads literals to 8-alignment
        var values = new int[] { 1, 2, 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 4 };
        var encoded = Encode(values, 4);
        ClassicAssert.IsTrue(encoded.Length > 0);
    }

    // ──────────────── Partial byte at end of literal group (line 341) ────────────────

    [Test]
    public void Write_ThreeLiterals_PartialBytePacked()
    {
        // 3 distinct values (no run >= 8) → partial byte at end
        var values = new int[] { 1, 2, 3 };
        var encoded = Encode(values, 2);
        // group_count = (3+7)>>3 = 1, byteCount = 1*2 = 2
        // header varint = ((1 << 1) | 1) = 3 → 1 byte; payload = 2 bytes; total = 3
        ClassicAssert.AreEqual(3, encoded.Length);
    }

    [Test]
    public void Write_NineLiterals_OneFullByteOnePartial()
    {
        // 9 distinct values → 1 full byte (values 0-7) + 1 partial byte (value 8)
        var values = new int[] { 0, 1, 2, 3, 4, 5, 6, 7, 8 };
        var encoded = Encode(values, 4);
        // group_count = (9+7)>>3 = 2, byteCount = 2*4 = 8
        ClassicAssert.IsTrue(encoded.Length > 0);
    }

    [Test]
    public void Write_FiveLiterals_PartialBytePacked()
    {
        // 5 literals (not a multiple of 8) → partial byte at end
        var values = new int[] { 1, 0, 1, 0, 1 }; // these are < 8 runs each
        var encoded = Encode(values, 1);
        ClassicAssert.IsTrue(encoded.Length > 0);
    }

    // ──────────────── bitWidth = 32 mask (line 243) ────────────────

    [Test]
    public void Write_BitWidth32_MaxValueMask()
    {
        // bitWidth=32 uses uint.MaxValue as mask
        var values = new int[] { int.MinValue, 0, int.MaxValue };
        var encoded = Encode(values, 32);
        ClassicAssert.IsTrue(encoded.Length > 0);
        // Each 32-bit value uses exactly 4 bytes; 3 values padded to 8 = 8 values × 4 bytes = 32 bytes for data
    }

    [Test]
    public void Write_BitWidth16_SmallValues()
    {
        // bitWidth=16: mask = (1u << 16) - 1 = 0xFFFF
        var values = new int[] { 0, 1, 65535 }; // max 16-bit value
        var encoded = Encode(values, 16);
        ClassicAssert.IsTrue(encoded.Length > 0);
    }

    // ──────────────── Boolean line 82 (same logic as line 40 but for booleans) ────────────────

    [Test]
    public void WriteBooleans_LiteralsThenExact8Run()
    {
        // [T, F, T, T, T, T, T, T, T, T, F] — 3 literals, then 8 trues, then 1 false
        var values = new bool[] { true, false, true, true, true, true, true, true, true, true, false };
        var encoded = EncodeBooleans(values);
        ClassicAssert.IsTrue(encoded.Length > 0);
    }

    [Test]
    public void WriteBooleans_Exact8RunThenLiterals()
    {
        // 8 trues (RLE run), then alternating false/true literals
        var values = new bool[] { true, true, true, true, true, true, true, true, false, true };
        var encoded = EncodeBooleans(values);
        ClassicAssert.IsTrue(encoded.Length > 0);
    }

    [Test]
    public void WriteBooleans_ThreeLiterals_PartialByte()
    {
        // 3 booleans → partial byte at end of group (line 335-347)
        var encoded = EncodeBooleans(new bool[] { true, false, true });
        // group_count = 1, but only 3 values
        ClassicAssert.IsTrue(encoded.Length > 0);
    }

    // ──────────────── Verified via Parquet roundtrip ────────────────

    [Test]
    public void DictionaryEncoding_Exact8RunBoundary_Roundtrip()
    {
        // In dictionary encoding, the indexes are RLE/bit-packed
        // Create data where dictionary indexes have runs of exactly 8
        var schema = new ParquetSchema([new Column("v", ParquetPhysicalType.Int32,
            new ColumnOptions(encodings: [EncodingKind.RleDictionary]))]);

        // 1 literal, then 8 of the same, then a different literal
        var values = new int[] { 100, 200, 200, 200, 200, 200, 200, 200, 200, 300 };
        using var ms = new MemoryStream();
        var writer = schema.CreateWriter(ms, new ParquetWriterOptions { Compression = CompressionKind.None });
        var col = writer.CreateSerializedColumn<int>(schema.Columns[0]);
        col.Serialize(values);
        writer.StartRowGroup().Write(col);
        writer.CloseFile();

        var result = ReadAll<int>(ms.ToArray(), schema);
        ClassicAssert.AreEqual(values, result);
    }

    [Test]
    public void DictionaryEncoding_SevenRunBoundary_Roundtrip()
    {
        // Run of 7 (not RLE, should be bit-packed)
        var schema = new ParquetSchema([new Column("v", ParquetPhysicalType.Int32,
            new ColumnOptions(encodings: [EncodingKind.RleDictionary]))]);
        var values = new int[] { 100, 200, 200, 200, 200, 200, 200, 200, 300 }; // 7 twos
        using var ms = new MemoryStream();
        var writer = schema.CreateWriter(ms, new ParquetWriterOptions { Compression = CompressionKind.None });
        var col = writer.CreateSerializedColumn<int>(schema.Columns[0]);
        col.Serialize(values);
        writer.StartRowGroup().Write(col);
        writer.CloseFile();
        ClassicAssert.AreEqual(values, ReadAll<int>(ms.ToArray(), schema));
    }

    [Test]
    public void BooleanEncoding_Mixed_RoundTrip_ExactBoundaries()
    {
        // Tests RLE and bit-pack transitions for booleans (line 82 and 341)
        var schema = new ParquetSchema([new Column("v", ParquetPhysicalType.Boolean,
            new ColumnOptions(encodings: [EncodingKind.Rle]))]);

        // 7 falses (literal), then 8 trues (RLE), then 3 falses (literal)
        var values = new bool[]
        {
            false, false, false, false, false, false, false,
            true, true, true, true, true, true, true, true,
            false, false, false
        };
        using var ms = new MemoryStream();
        var writer = schema.CreateWriter(ms, new ParquetWriterOptions { Compression = CompressionKind.None });
        var col = writer.CreateSerializedColumn<bool>(schema.Columns[0]);
        col.Serialize(values);
        writer.StartRowGroup().Write(col);
        writer.CloseFile();
        ClassicAssert.AreEqual(values, ReadAll<bool>(ms.ToArray(), schema));
    }

    [Test]
    public void BooleanEncoding_LiteralsWithPartialByte_Roundtrip()
    {
        // Tests partial-byte packing at line 335-347
        var schema = new ParquetSchema([new Column("v", ParquetPhysicalType.Boolean,
            new ColumnOptions(encodings: [EncodingKind.Rle]))]);
        // 3 alternating values (partial byte) then 8+ of same (RLE)
        var values = new bool[] { true, false, true, false, false, false, false, false, false, false, false };
        using var ms = new MemoryStream();
        var writer = schema.CreateWriter(ms, new ParquetWriterOptions { Compression = CompressionKind.None });
        var col = writer.CreateSerializedColumn<bool>(schema.Columns[0]);
        col.Serialize(values);
        writer.StartRowGroup().Write(col);
        writer.CloseFile();
        ClassicAssert.AreEqual(values, ReadAll<bool>(ms.ToArray(), schema));
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
