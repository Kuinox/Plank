using Plank.Fuzzing;

namespace Plank.Tests.Fuzzing;

internal sealed class PlankReaderFuzzTargetTests
{
    static byte[] Fixture(string name)
        => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fuzzing", "Fixtures", name + ".parquet"));

    [Test]
    public void EmptyInputDoesNotCrash()
        => PlankReaderFuzzTarget.Execute([]);

    [Test]
    public void AllZeroInputDoesNotCrash()
        => PlankReaderFuzzTarget.Execute(new byte[64]);

    [Test]
    public void TruncatedMagicDoesNotCrash()
        => PlankReaderFuzzTarget.Execute([0x00, 0x50, 0x41, 0x52, 0x31]); // schema 0, "PAR1" only

    [Test]
    public void AllGeneratedSeedsAreReadableWithoutCrash()
    {
        foreach (var (name, bytes) in ReaderSeedGenerator.AllSeeds())
        {
            try
            {
                PlankReaderFuzzTarget.Execute(bytes);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Reader seed '{name}' caused an unexpected crash.", ex);
            }
        }
    }

    [Test]
    public void NegativeI64OffsetInFooterDoesNotCrash()
        // Footer encodes a negative row group file_offset; used to throw ArgumentOutOfRangeException
        // from RowGroupToken..ctor before ReadI64AsU64 validation was added.
        => PlankReaderFuzzTarget.Execute(Fixture("negative-i64-offset-in-footer"));

    [Test]
    public void NegativeCompressedPageSizeDoesNotCrash()
        // Page header encodes a negative compressed_page_size; used to throw ArgumentOutOfRangeException
        // from buffer.AsSpan() before ReadI32AsU32(max: reader.Remaining) validation was added.
        => PlankReaderFuzzTarget.Execute(Fixture("negative-compressed-page-size"));

    [Test]
    public void DefinitionLevelLiteralByteCountExceedsPayloadDoesNotCrash()
        // ReadDefinitionLevels: literal group claims more bytes than remain in payload;
        // used to throw ArgumentOutOfRangeException from payload slice.
        => PlankReaderFuzzTarget.Execute(Fixture("def-level-literal-byte-count-exceeds-payload"));

    [Test]
    public void DefinitionLevelLiteralGroupCountTooLargeDoesNotCrash()
        // ReadDefinitionLevels: literal group count * 8 overflows uint;
        // used to throw OverflowException from checked multiplication.
        => PlankReaderFuzzTarget.Execute(Fixture("def-level-literal-group-count-too-large"));

    [Test]
    public void SnappyDestinationTooSmallDoesNotCrash()
        // Snappy decompressor throws ArgumentException when actual uncompressed size exceeds
        // expectedLength; used to escape the InvalidDataException-only catch in Decompress().
        => PlankReaderFuzzTarget.Execute(Fixture("snappy-destination-too-small"));

    [Test]
    public void PlainInt32PayloadTooShortDoesNotCrash()
        // DecodePlainInt32: payload too short for valueCount plain Int32 values;
        // used to throw ArgumentOutOfRangeException from BinaryPrimitives.ReadInt32LittleEndian.
        => PlankReaderFuzzTarget.Execute(Fixture("plain-int32-payload-too-short"));

    [Test]
    public void ByteStreamSplitInt32PayloadTooShortDoesNotCrash()
        // DecodeByteStreamSplit Int32 path: no bounds check; used to throw IndexOutOfRangeException.
        => PlankReaderFuzzTarget.Execute(Fixture("byte-stream-split-int32-payload-too-short"));

    [Test]
    public void PlainInt64PayloadTooShortDoesNotCrash()
        // DecodePlainInt64: payload too short for valueCount plain Int64 values;
        // used to throw ArgumentOutOfRangeException from BinaryPrimitives.ReadInt64LittleEndian.
        => PlankReaderFuzzTarget.Execute(Fixture("plain-int64-payload-too-short"));

    [Test]
    public void DictionaryIndexesWithNullsOutOfBoundsDoesNotCrash()
        // DecodeDictionaryIndexesWithNulls: definition levels claim more non-null values than
        // physicalValueCount, or dictionary index out of range; used to throw IndexOutOfRangeException.
        => PlankReaderFuzzTarget.Execute(Fixture("dictionary-indexes-nulls-out-of-bounds"));

    [Test]
    public void PlainDoublePayloadTooShortDoesNotCrash()
        // DecodePlainDouble: payload too short for valueCount plain Double values;
        // used to throw ArgumentOutOfRangeException from BinaryPrimitives.ReadInt64LittleEndian.
        => PlankReaderFuzzTarget.Execute(Fixture("plain-double-payload-too-short"));

    [Test]
    public void RleBitPackedHybridZeroBitWidthDoesNotCrash()
        // ReadRleBitPackedHybrid: bitWidth==0 caused DivideByZeroException in overflow check.
        => PlankReaderFuzzTarget.Execute(Fixture("rle-bit-packed-hybrid-zero-bit-width"));

    [Test]
    public void RowGroupCountOverflowDoesNotCrash()
        // ReadRowGroups: count > int.MaxValue caused OverflowException from checked((int)count).
        => PlankReaderFuzzTarget.Execute(Fixture("row-group-count-overflow"));

    [Test]
    [Explicit]
    public void WriteReaderCorpusSeeds()
        => ReaderSeedGenerator.WriteSeeds();

}
