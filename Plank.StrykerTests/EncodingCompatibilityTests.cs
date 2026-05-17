using Plank.Schema;

namespace Plank.StrykerTests;

public class EncodingCompatibilityTests
{
    [Theory]
    [InlineData(ParquetPhysicalType.Boolean, EncodingKind.Plain, true)]
    [InlineData(ParquetPhysicalType.Int32, EncodingKind.Plain, true)]
    [InlineData(ParquetPhysicalType.Int64, EncodingKind.Plain, true)]
    [InlineData(ParquetPhysicalType.Float, EncodingKind.Plain, true)]
    [InlineData(ParquetPhysicalType.Double, EncodingKind.Plain, true)]
    [InlineData(ParquetPhysicalType.ByteArray, EncodingKind.Plain, true)]
    public void Plain_SupportedForAllTypes(ParquetPhysicalType type, EncodingKind encoding, bool expected)
        => Assert.Equal(expected, EncodingCompatibility.IsSupported(type, encoding));

    [Theory]
    [InlineData(ParquetPhysicalType.Boolean, EncodingKind.Rle, true)]
    [InlineData(ParquetPhysicalType.Int32, EncodingKind.Rle, false)]
    [InlineData(ParquetPhysicalType.ByteArray, EncodingKind.Rle, false)]
    public void Rle_OnlySupportedForBoolean(ParquetPhysicalType type, EncodingKind encoding, bool expected)
        => Assert.Equal(expected, EncodingCompatibility.IsSupported(type, encoding));

    [Theory]
    [InlineData(ParquetPhysicalType.Int32, EncodingKind.DeltaBinaryPacked, true)]
    [InlineData(ParquetPhysicalType.Int64, EncodingKind.DeltaBinaryPacked, true)]
    [InlineData(ParquetPhysicalType.Float, EncodingKind.DeltaBinaryPacked, false)]
    [InlineData(ParquetPhysicalType.Double, EncodingKind.DeltaBinaryPacked, false)]
    [InlineData(ParquetPhysicalType.ByteArray, EncodingKind.DeltaBinaryPacked, false)]
    [InlineData(ParquetPhysicalType.Boolean, EncodingKind.DeltaBinaryPacked, false)]
    public void DeltaBinaryPacked_OnlySupportedForInt32AndInt64(ParquetPhysicalType type, EncodingKind encoding, bool expected)
        => Assert.Equal(expected, EncodingCompatibility.IsSupported(type, encoding));

    [Theory]
    [InlineData(ParquetPhysicalType.ByteArray, EncodingKind.DeltaLengthByteArray, true)]
    [InlineData(ParquetPhysicalType.Int32, EncodingKind.DeltaLengthByteArray, false)]
    [InlineData(ParquetPhysicalType.ByteArray, EncodingKind.DeltaByteArray, true)]
    [InlineData(ParquetPhysicalType.Int32, EncodingKind.DeltaByteArray, false)]
    public void DeltaByteArray_OnlySupportedForByteArray(ParquetPhysicalType type, EncodingKind encoding, bool expected)
        => Assert.Equal(expected, EncodingCompatibility.IsSupported(type, encoding));

    [Theory]
    [InlineData(ParquetPhysicalType.Int32, EncodingKind.ByteStreamSplit, true)]
    [InlineData(ParquetPhysicalType.Int64, EncodingKind.ByteStreamSplit, true)]
    [InlineData(ParquetPhysicalType.Float, EncodingKind.ByteStreamSplit, true)]
    [InlineData(ParquetPhysicalType.Double, EncodingKind.ByteStreamSplit, true)]
    [InlineData(ParquetPhysicalType.FixedLenByteArray, EncodingKind.ByteStreamSplit, true)]
    [InlineData(ParquetPhysicalType.Boolean, EncodingKind.ByteStreamSplit, false)]
    [InlineData(ParquetPhysicalType.ByteArray, EncodingKind.ByteStreamSplit, false)]
    public void ByteStreamSplit_SupportedTypes(ParquetPhysicalType type, EncodingKind encoding, bool expected)
        => Assert.Equal(expected, EncodingCompatibility.IsSupported(type, encoding));

    [Fact]
    public void BitPacked_NotSupportedForAnyType()
    {
        foreach (var type in Enum.GetValues<ParquetPhysicalType>())
            Assert.False(EncodingCompatibility.IsSupported(type, EncodingKind.BitPacked));
    }

    [Fact]
    public void Validate_ThrowsForInvalidCombination()
    {
        // Column constructor calls Validate internally, so the exception is thrown there.
        Assert.Throws<NotSupportedException>(() =>
            new Column("x", ParquetPhysicalType.Boolean,
                new ColumnOptions(encodings: [EncodingKind.DeltaBinaryPacked])));
    }

    [Fact]
    public void Validate_DoesNotThrowForValidCombination()
    {
        var col = new Column("x", ParquetPhysicalType.Int32,
            new ColumnOptions(encodings: [EncodingKind.DeltaBinaryPacked]));
        EncodingCompatibility.Validate(col);
    }
}
