using Plank.Reading;

namespace Plank.StrykerTests;

/// <summary>
/// Direct unit tests for CompactProtocolReader (Thrift compact protocol decoder).
/// Targets surviving mutants in varint decoding, zig-zag decode, field headers, list headers.
/// </summary>
public class CompactProtocolReaderTests
{
    // ──────────────── ReadVarU32 ────────────────

    [Test]
    public void ReadVarU32_Zero_ReturnsZero()
    {
        var reader = new CompactProtocolReader(new byte[] { 0x00 });
        ClassicAssert.AreEqual(0u, reader.ReadVarU32());
    }

    [Test]
    public void ReadVarU32_One_ReturnsOne()
    {
        var reader = new CompactProtocolReader(new byte[] { 0x01 });
        ClassicAssert.AreEqual(1u, reader.ReadVarU32());
    }

    [Test]
    public void ReadVarU32_127_SingleByte()
    {
        var reader = new CompactProtocolReader(new byte[] { 0x7F });
        ClassicAssert.AreEqual(127u, reader.ReadVarU32());
    }

    [Test]
    public void ReadVarU32_128_TwoBytes()
    {
        // 128 = 0x80 → encoded as 0x80, 0x01
        var reader = new CompactProtocolReader(new byte[] { 0x80, 0x01 });
        ClassicAssert.AreEqual(128u, reader.ReadVarU32());
    }

    [Test]
    public void ReadVarU32_300_TwoBytes()
    {
        // 300 = 0x12C → encoded as 0xAC, 0x02
        var reader = new CompactProtocolReader(new byte[] { 0xAC, 0x02 });
        ClassicAssert.AreEqual(300u, reader.ReadVarU32());
    }

    [Test]
    public void ReadVarU32_ExceedsMax_Throws()
    {
        var reader = new CompactProtocolReader(new byte[] { 0x80, 0x01 }); // 128
        try { reader.ReadVarU32(max: 100); Assert.Fail("Expected CorruptParquetException"); }
        catch (CorruptParquetException) { }
    }

    [Test]
    public void ReadVarU32_AtMax_ReturnsValue()
    {
        var reader = new CompactProtocolReader(new byte[] { 0x64 }); // 100
        ClassicAssert.AreEqual(100u, reader.ReadVarU32(max: 100));
    }

    // ──────────────── ReadI32 (zig-zag decoded) ────────────────

    [Test]
    public void ReadI32_Zero_ReturnsZero()
    {
        var reader = new CompactProtocolReader(new byte[] { 0x00 });
        ClassicAssert.AreEqual(0, reader.ReadI32());
    }

    [Test]
    public void ReadI32_One_ReturnsOne()
    {
        // ZigZag: 1 encoded as 2 (0x02)
        var reader = new CompactProtocolReader(new byte[] { 0x02 });
        ClassicAssert.AreEqual(1, reader.ReadI32());
    }

    [Test]
    public void ReadI32_MinusOne_ReturnsMinusOne()
    {
        // ZigZag: -1 encoded as 1 (0x01)
        var reader = new CompactProtocolReader(new byte[] { 0x01 });
        ClassicAssert.AreEqual(-1, reader.ReadI32());
    }

    [Test]
    public void ReadI32_Two_ReturnsTwo()
    {
        // ZigZag: 2 encoded as 4 (0x04)
        var reader = new CompactProtocolReader(new byte[] { 0x04 });
        ClassicAssert.AreEqual(2, reader.ReadI32());
    }

    [Test]
    public void ReadI32_MinusTwo_ReturnsMinusTwo()
    {
        // ZigZag: -2 encoded as 3 (0x03)
        var reader = new CompactProtocolReader(new byte[] { 0x03 });
        ClassicAssert.AreEqual(-2, reader.ReadI32());
    }

    [Test]
    public void ReadI32_LargePositive()
    {
        // 1000 in ZigZag = 2000 = 0x07D0 → LEB128: 0xD0 (| 0x80 → 0xD0), 0x0F
        var reader = new CompactProtocolReader(new byte[] { 0xD0, 0x0F });
        ClassicAssert.AreEqual(1000, reader.ReadI32());
    }

    [Test]
    public void ReadI32AsU32_NonNegative_Succeeds()
    {
        // 5 in ZigZag = 10 (0x0A)
        var reader = new CompactProtocolReader(new byte[] { 0x0A });
        ClassicAssert.AreEqual(5u, reader.ReadI32AsU32());
    }

    [Test]
    public void ReadI32AsU32_Negative_Throws()
    {
        // -1 in ZigZag = 1 (0x01)
        var reader = new CompactProtocolReader(new byte[] { 0x01 });
        try { reader.ReadI32AsU32(); Assert.Fail("Expected CorruptParquetException"); }
        catch (CorruptParquetException) { }
    }

    // ──────────────── ReadI64 ────────────────

    [Test]
    public void ReadI64_Zero_ReturnsZero()
    {
        var reader = new CompactProtocolReader(new byte[] { 0x00 });
        ClassicAssert.AreEqual(0L, reader.ReadI64());
    }

    [Test]
    public void ReadI64_MinusOne_ReturnsMinusOne()
    {
        var reader = new CompactProtocolReader(new byte[] { 0x01 });
        ClassicAssert.AreEqual(-1L, reader.ReadI64());
    }

    [Test]
    public void ReadI64AsU64_NonNegative_Succeeds()
    {
        var reader = new CompactProtocolReader(new byte[] { 0x0A }); // 5 in zigzag
        ClassicAssert.AreEqual(5uL, reader.ReadI64AsU64());
    }

    [Test]
    public void ReadI64AsU64_Negative_Throws()
    {
        var reader = new CompactProtocolReader(new byte[] { 0x01 }); // -1 in zigzag
        try { reader.ReadI64AsU64(); Assert.Fail("Expected CorruptParquetException"); }
        catch (CorruptParquetException) { }
    }

    // ──────────────── TryReadFieldHeader ────────────────

    [Test]
    public void TryReadFieldHeader_StopByte_ReturnsFalse()
    {
        var reader = new CompactProtocolReader(new byte[] { 0x00 });
        var prevId = 0;
        ClassicAssert.IsFalse(reader.TryReadFieldHeader(ref prevId, out _, out _, out _));
    }

    [Test]
    public void TryReadFieldHeader_DeltaField_ComputesFieldId()
    {
        // byte = 0x13: high nibble=1 (delta), low nibble=3 (Byte type = 3)
        // fieldId = prevId + 1 = 0 + 1 = 1
        var reader = new CompactProtocolReader(new byte[] { 0x13 });
        var prevId = 0;
        ClassicAssert.IsTrue(reader.TryReadFieldHeader(ref prevId, out var fieldId, out var type, out _));
        ClassicAssert.AreEqual(1, fieldId);
        ClassicAssert.AreEqual(CompactProtocolType.Byte, type);
    }

    [Test]
    public void TryReadFieldHeader_DeltaTwo_AddsTwo()
    {
        // byte = 0x23: high nibble=2, low nibble=3 (Byte = 3)
        // fieldId = prevId + 2 = 5 + 2 = 7
        var reader = new CompactProtocolReader(new byte[] { 0x23 });
        var prevId = 5;
        ClassicAssert.IsTrue(reader.TryReadFieldHeader(ref prevId, out var fieldId, out _, out _));
        ClassicAssert.AreEqual(7, fieldId);
    }

    [Test]
    public void TryReadFieldHeader_BooleanTrue_InlineBoolIsTrue()
    {
        // Type = BooleanTrue (0x01), delta = 1 → byte = 0x11
        var reader = new CompactProtocolReader(new byte[] { 0x11 });
        var prevId = 0;
        reader.TryReadFieldHeader(ref prevId, out _, out _, out var inlineBool);
        ClassicAssert.IsTrue(inlineBool);
    }

    [Test]
    public void TryReadFieldHeader_BooleanFalse_InlineBoolIsFalse()
    {
        // Type = BooleanFalse (0x02), delta = 1 → byte = 0x12
        var reader = new CompactProtocolReader(new byte[] { 0x12 });
        var prevId = 0;
        reader.TryReadFieldHeader(ref prevId, out _, out _, out var inlineBool);
        ClassicAssert.IsFalse(inlineBool);
    }

    [Test]
    public void TryReadFieldHeader_UpdatesPreviousFieldId()
    {
        // Two fields: delta=1, delta=1 → field 1, then field 2
        // Use type=Byte (3) to avoid BooleanTrue/False inline behavior
        var reader = new CompactProtocolReader(new byte[] { 0x13, 0x13 });
        var prevId = 0;
        reader.TryReadFieldHeader(ref prevId, out var id1, out _, out _);
        reader.TryReadFieldHeader(ref prevId, out var id2, out _, out _);
        ClassicAssert.AreEqual(1, id1);
        ClassicAssert.AreEqual(2, id2);
    }

    // ──────────────── ReadListHeader ────────────────

    [Test]
    public void ReadListHeader_ShortForm_CountInNibble()
    {
        // 0x33: high nibble=3 (count), low nibble=3 (Byte type = 3)
        var reader = new CompactProtocolReader(new byte[] { 0x33 });
        var (count, type) = reader.ReadListHeader();
        ClassicAssert.AreEqual(3u, count);
        ClassicAssert.AreEqual(CompactProtocolType.Byte, type);
    }

    [Test]
    public void ReadListHeader_ZeroCount()
    {
        // 0x03: high nibble=0, low nibble=3 (Byte)
        var reader = new CompactProtocolReader(new byte[] { 0x03 });
        var (count, type) = reader.ReadListHeader();
        ClassicAssert.AreEqual(0u, count);
        ClassicAssert.AreEqual(CompactProtocolType.Byte, type);
    }

    [Test]
    public void ReadListHeader_FourteenElements_ShortForm()
    {
        // 0xE3: high nibble=14, low nibble=3 (Byte)
        var reader = new CompactProtocolReader(new byte[] { 0xE3 });
        var (count, type) = reader.ReadListHeader();
        ClassicAssert.AreEqual(14u, count);
    }

    [Test]
    public void ReadListHeader_FifteenNibble_ReadsVarU32()
    {
        // 0xF1: high nibble=15 (→ read varint), low nibble=1
        // Then varint 0x10 = 16
        var reader = new CompactProtocolReader(new byte[] { 0xF1, 0x10 });
        var (count, _) = reader.ReadListHeader();
        ClassicAssert.AreEqual(16u, count);
    }

    // ──────────────── Offset tracking ────────────────

    [Test]
    public void Offset_AfterReads_IsCorrect()
    {
        var reader = new CompactProtocolReader(new byte[] { 0x02, 0x04 }); // two i32 values (1 and 2 in zigzag)
        reader.ReadI32();
        ClassicAssert.AreEqual(1, reader.Offset);
        reader.ReadI32();
        ClassicAssert.AreEqual(2, reader.Offset);
    }

    [Test]
    public void Remaining_DecreasesAfterRead()
    {
        var reader = new CompactProtocolReader(new byte[] { 0x00, 0x00, 0x00 });
        ClassicAssert.AreEqual(3u, reader.Remaining);
        reader.ReadVarU32();
        ClassicAssert.AreEqual(2u, reader.Remaining);
    }
}
