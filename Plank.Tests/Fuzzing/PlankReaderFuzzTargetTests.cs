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
    [Explicit]
    public void WriteReaderCorpusSeeds()
        => ReaderSeedGenerator.WriteSeeds();
}
