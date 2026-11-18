using Plank.Reading;
using Plank.Schema;

namespace Plank.Tests.Reading;

// Snappier before 1.3.2 decoded these corrupt blocks into plausible-looking data
// instead of rejecting them, so Plank accepted pages that Arrow and Google's snappy
// reject. Found by the Plank-Lab reader differential fuzzer; fixed upstream in
// https://github.com/brantburnett/Snappier/pull/156 (issue #152).
internal sealed class SnappyCorruptionTests
{
    public static IEnumerable<Func<byte[]>> LiteralOverrunBlocks()
    {
        // Declared length 10, literal tag 0x44 declares 18 bytes, only 10 follow.
        yield return () => [0x0a, 0x44, 0x01, 0x00, 0x00, 0x00, 0x01, 0x01, 0x00, 0x00, 0x00, 0x02];
        // Declared length 10, literal tag 0x34 declares 14 bytes, all present.
        yield return () => [0x0a, 0x34, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0a, 0x0b, 0x0c, 0x0d, 0x0e];
        // Declared length 1, long literal with 4-byte length 0xFFFFFFFF, then a valid 1-byte literal.
        yield return () => [0x01, 0xFC, 0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0x42];
    }

    [Test]
    [MethodDataSource(nameof(LiteralOverrunBlocks))]
    public void LiteralOverrunIsCorrupt(byte[] payload)
    {
        var destination = new byte[payload[0]];
        Assert.Throws<CorruptParquetException>(() =>
            ParquetDecompressor.DecompressInto(payload, CompressionKind.Snappy, destination));
    }

    [Test]
    public void CompleteLiteralStillDecodes()
    {
        byte[] payload = [0x05, 0x10, 1, 2, 3, 4, 5];
        var destination = new byte[5];
        ParquetDecompressor.DecompressInto(payload, CompressionKind.Snappy, destination);
        if (!destination.AsSpan().SequenceEqual(new byte[] { 1, 2, 3, 4, 5 }))
            throw new InvalidOperationException("Snappy literal did not round-trip.");
    }
}
