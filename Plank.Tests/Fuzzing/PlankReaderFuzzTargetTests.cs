using Plank.Fuzzing;

namespace Plank.Tests.Fuzzing;

internal sealed class PlankReaderFuzzTargetTests
{
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
        => PlankReaderFuzzTarget.Execute(Convert.FromHexString(
            "044041523115041518150000f90615184cfafafa0000020001000300ff001506150f1500193102" +
            "191804010000001918040300e9ff1400190c0000191c163a1545160000001502192c4806736368" +
            "656d611502001502250018026330001618191c191c263a1c1502192500000615186400fafa0000" +
            "02000100030000001506150f150019f9060000000000000000000000000000001506150215025c" +
            "151015001510159e010000a900000050415231006b00000050415231"));

    [Test]
    public void NegativeCompressedPageSizeDoesNotCrash()
        // Page header encodes a negative compressed_page_size; used to throw ArgumentOutOfRangeException
        // from buffer.AsSpan() before ReadI32AsU32(max: reader.Remaining) validation was added.
        => PlankReaderFuzzTarget.Execute(Convert.FromHexString(
            "04504152311504152d15184c15150015001200000205048685001911021918040100000019180403" +
            "000000150019160000191c163a153816ffe1001502192c4806736368656d611502001502250018" +
            "026330001618191c191c263a1c150219250010191802633015081618166a166a263a26081c1801" +
            "0300000018040100000016002804030000001804010000001111000016a00115141672152e00166a" +
            "1618263a166a00006b00000050415231"));

    [Test]
    public void DefinitionLevelLiteralByteCountExceedsPayloadDoesNotCrash()
        // ReadDefinitionLevels: literal group claims more bytes than remain in payload;
        // used to throw ArgumentOutOfRangeException from payload slice.
        => PlankReaderFuzzTarget.Execute(Convert.FromHexString(
            "01504152311506154415445c151015101510150a15161500120000000000001502193c4806736368" +
            "656d611504001502250018026330001500250018026331001610191c192c26081c150219150a1918" +
            "026330150016101670167026083c18046400000018040000000016042804640000001804000000001111" +
            "000016d401151416a601152e0026781c1500191500191802633115001610162e164e26783c18010118" +
            "0100160028010118010011110000168a02151416e801152200169e0116102608169e010000a9000000" +
            "50415231"));

    [Test]
    public void DefinitionLevelLiteralGroupCountTooLargeDoesNotCrash()
        // ReadDefinitionLevels: literal group count * 8 overflows uint;
        // used to throw OverflowException from checked multiplication.
        => PlankReaderFuzzTarget.Execute(Convert.FromHexString(
            "01504152311506154415445c151015101510150a15161500120000ffffff801502193c4806736368" +
            "656d611504001502250018026330001500250018026331001610191c192c26081c150219150a1918" +
            "026330150016101670167026083c18046400000018040000000016042804640000001804000000001111" +
            "000016d401151416a601152e0026781c1500191500191802633115001610162e164e26783c18010118" +
            "0100160028010118010011110000168a02151416e801152200169e0116102608169e010000a9000000" +
            "50415231"));

    [Test]
    public void SnappyDestinationTooSmallDoesNotCrash()
        // Snappy decompressor throws ArgumentException when actual uncompressed size exceeds
        // expectedLength; used to escape the InvalidDataException-only catch in Decompress().
        => PlankReaderFuzzTarget.Execute(Convert.FromHexString(
            "04504152311504151815184c1506150000001200000002000000030000001506150c550c00150000" +
            "000300000015061580ffffff1518150015181510001502192c4806736368656d611502001502250018" +
            "026330001618191c191c263a1c150219250010191802633015021618166a166a263a26081c18040300" +
            "000018040100000016002804030000001804010000001111000016a00115141672152e00166a1618263a" +
            "166a00006b00000050415231"));

    [Test]
    public void PlainInt32PayloadTooShortDoesNotCrash()
        // DecodePlainInt32: payload too short for valueCount plain Int32 values;
        // used to throw ArgumentOutOfRangeException from BinaryPrimitives.ReadInt32LittleEndian.
        => PlankReaderFuzzTarget.Execute(Convert.FromHexString(
            "b3504152311506154415445c152415101510100015161500120000800000001502193c4806736368" +
            "656d611504001502250018026330001500251502026331001610191c192c26081c150219150a1918" +
            "026330150016101670167026083c18046400000018040000000016042804640000001804000000001111" +
            "000016d401151416a601152e0026781c1500191500191802633115001610162e164e26783c18010118" +
            "0100160028010118010011110000168a02151416e801152200169e0116102608169e010000a9000000" +
            "50415231"));

    [Test]
    public void ByteStreamSplitInt32PayloadTooShortDoesNotCrash()
        // DecodeByteStreamSplit Int32 path: no bounds check; used to throw IndexOutOfRangeException.
        => PlankReaderFuzzTarget.Execute(Convert.FromHexString(
            "02504152311506154415445c153615101510101215161500120000800000001502193c4806736368" +
            "656d611504001502250018026330001500251502026331001610191c192c26081c150219150a1918" +
            "026330150016101670167026083c18046400000018040000000016042804640000001804000000001111" +
            "000016d401151416a601152e0026781c1500191500191802633115001610162e164e26783c18010118" +
            "0100160028010118010011110000168a02151416e801152200169e0116102608169e010000a9000000" +
            "50415231"));

    [Test]
    public void PlainInt64PayloadTooShortDoesNotCrash()
        // DecodePlainInt64: payload too short for valueCount plain Int64 values;
        // used to throw ArgumentOutOfRangeException from BinaryPrimitives.ReadInt64LittleEndian.
        => PlankReaderFuzzTarget.Execute(Convert.FromHexString(
            "b3504152311506154415445c151015041510100015161500120000800000001502193c4806736368" +
            "656d611504001502252318026330001500251502026331001610191c192c26081c150219150a1918" +
            "026330150016101670167026003c18046400000018040000000016042804640000001804000000001111" +
            "000016d401151416a6013115001610162e164e26983c18010118010016002801011801001111000016" +
            "8a02151416e801152200169e0116102608169e0100009a00000050415231"));

    [Test]
    public void DictionaryIndexesWithNullsOutOfBoundsDoesNotCrash()
        // DecodeDictionaryIndexesWithNulls: definition levels claim more non-null values than
        // physicalValueCount, or dictionary index out of range; used to throw IndexOutOfRangeException.
        => PlankReaderFuzzTarget.Execute(Convert.FromHexString(
            "04504152311504151815184c1506150000000100000000000000030000001506150c150c5c1518150e" +
            "15181510150c1500120000020524861502192c4806736368654c631502001502250018026330001618" +
            "191c191c263a1c150219250010191802633015001618166a166a263a26081c18040300000018040100" +
            "000016e22804030000001804010000001111007f00000015141672152e00166a1618263a166a000060" +
            "00000050415231"));

    [Test]
    public void PlainDoublePayloadTooShortDoesNotCrash()
        // DecodePlainDouble: payload too short for valueCount plain Double values;
        // used to throw ArgumentOutOfRangeException from BinaryPrimitives.ReadInt64LittleEndian.
        => PlankReaderFuzzTarget.Execute(Convert.FromHexString(
            "03504152311506154415445c153415101510100015161500120000800000001502193c4806736368" +
            "656d611504001502250018026330001500251502026331001610191c192c260011110000168a021508" +
            "1c150219150a1918026330150016101670167026083c18046100000018040001000016042804640000" +
            "001804000000001111000016d401151416a601152e0026781c1500191500191802633115001610162e" +
            "164e26783c180101180100160028010118010011110000168a02151416e801152200169e0116102608" +
            "169e010000b200000050415231"));

    [Test]
    public void RleBitPackedHybridZeroBitWidthDoesNotCrash()
        // ReadRleBitPackedHybrid: bitWidth==0 caused DivideByZeroException in overflow check.
        => PlankReaderFuzzTarget.Execute(Convert.FromHexString(
            "04504152311504151815184c1506150000000100000000000000030000001506150c150c5c1518150e" +
            "15181510150815001200001502192c0009736368654c1c191c631502001502250018026330001618" +
            "191c191c263a1c150219250010191802633015001618166a166a263a26081c18040300000018040100" +
            "000016e22804030000001804010000001111000016a00115141672012e00166a1618263a166a000060" +
            "00000050415231"));

    [Test]
    [Explicit]
    public void WriteReaderCorpusSeeds()
        => ReaderSeedGenerator.WriteSeeds();
}
