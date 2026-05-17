using Plank.Schema;

namespace Plank.StrykerTests;

public class EncodingCompatibilityTests
{
    [TestCase(ParquetPhysicalType.Boolean, EncodingKind.Plain, true)]
    [TestCase(ParquetPhysicalType.Int32, EncodingKind.Plain, true)]
    [TestCase(ParquetPhysicalType.Int64, EncodingKind.Plain, true)]
    [TestCase(ParquetPhysicalType.Float, EncodingKind.Plain, true)]
    [TestCase(ParquetPhysicalType.Double, EncodingKind.Plain, true)]
    [TestCase(ParquetPhysicalType.ByteArray, EncodingKind.Plain, true)]
    public void Plain_SupportedForAllTypes(ParquetPhysicalType type, EncodingKind encoding, bool expected)
        => ClassicAssert.AreEqual(expected, EncodingCompatibility.IsSupported(type, encoding));

    [TestCase(ParquetPhysicalType.Boolean, EncodingKind.Rle, true)]
    [TestCase(ParquetPhysicalType.Int32, EncodingKind.Rle, false)]
    [TestCase(ParquetPhysicalType.ByteArray, EncodingKind.Rle, false)]
    public void Rle_OnlySupportedForBoolean(ParquetPhysicalType type, EncodingKind encoding, bool expected)
        => ClassicAssert.AreEqual(expected, EncodingCompatibility.IsSupported(type, encoding));

    [TestCase(ParquetPhysicalType.Int32, EncodingKind.DeltaBinaryPacked, true)]
    [TestCase(ParquetPhysicalType.Int64, EncodingKind.DeltaBinaryPacked, true)]
    [TestCase(ParquetPhysicalType.Float, EncodingKind.DeltaBinaryPacked, false)]
    [TestCase(ParquetPhysicalType.Double, EncodingKind.DeltaBinaryPacked, false)]
    [TestCase(ParquetPhysicalType.ByteArray, EncodingKind.DeltaBinaryPacked, false)]
    [TestCase(ParquetPhysicalType.Boolean, EncodingKind.DeltaBinaryPacked, false)]
    public void DeltaBinaryPacked_OnlySupportedForInt32AndInt64(ParquetPhysicalType type, EncodingKind encoding, bool expected)
        => ClassicAssert.AreEqual(expected, EncodingCompatibility.IsSupported(type, encoding));

    [TestCase(ParquetPhysicalType.ByteArray, EncodingKind.DeltaLengthByteArray, true)]
    [TestCase(ParquetPhysicalType.Int32, EncodingKind.DeltaLengthByteArray, false)]
    [TestCase(ParquetPhysicalType.ByteArray, EncodingKind.DeltaByteArray, true)]
    [TestCase(ParquetPhysicalType.Int32, EncodingKind.DeltaByteArray, false)]
    public void DeltaByteArray_OnlySupportedForByteArray(ParquetPhysicalType type, EncodingKind encoding, bool expected)
        => ClassicAssert.AreEqual(expected, EncodingCompatibility.IsSupported(type, encoding));

    [TestCase(ParquetPhysicalType.Int32, EncodingKind.ByteStreamSplit, true)]
    [TestCase(ParquetPhysicalType.Int64, EncodingKind.ByteStreamSplit, true)]
    [TestCase(ParquetPhysicalType.Float, EncodingKind.ByteStreamSplit, true)]
    [TestCase(ParquetPhysicalType.Double, EncodingKind.ByteStreamSplit, true)]
    [TestCase(ParquetPhysicalType.FixedLenByteArray, EncodingKind.ByteStreamSplit, true)]
    [TestCase(ParquetPhysicalType.Boolean, EncodingKind.ByteStreamSplit, false)]
    [TestCase(ParquetPhysicalType.ByteArray, EncodingKind.ByteStreamSplit, false)]
    public void ByteStreamSplit_SupportedTypes(ParquetPhysicalType type, EncodingKind encoding, bool expected)
        => ClassicAssert.AreEqual(expected, EncodingCompatibility.IsSupported(type, encoding));

    [Test]
    public void BitPacked_NotSupportedForAnyType()
    {
        foreach (var type in Enum.GetValues<ParquetPhysicalType>())
            ClassicAssert.IsFalse(EncodingCompatibility.IsSupported(type, EncodingKind.BitPacked));
    }

    [Test]
    public void Validate_ThrowsForInvalidCombination()
    {
        // Column constructor calls Validate internally, so the exception is thrown there.
        Assert.Throws<NotSupportedException>(() =>
            new Column("x", ParquetPhysicalType.Boolean,
                new ColumnOptions(encodings: [EncodingKind.DeltaBinaryPacked])));
    }

    [Test]
    public void Validate_DoesNotThrowForValidCombination()
    {
        var col = new Column("x", ParquetPhysicalType.Int32,
            new ColumnOptions(encodings: [EncodingKind.DeltaBinaryPacked]));
        EncodingCompatibility.Validate(col);
    }
}
