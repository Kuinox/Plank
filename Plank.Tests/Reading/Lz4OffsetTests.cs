using Plank.Reading;
using Plank.Schema;

namespace Plank.Tests.Reading;

internal sealed class Lz4OffsetTests
{
    [Test]
    public void RejectsZeroOffsetWithoutReturningPooledData()
    {
        // One literal, seven-byte match at offset zero, then five literals.
        byte[] block = [0x13, 1, 0, 0, 0x50, 2, 3, 4, 5, 6];
        byte[] destination = Enumerable.Repeat((byte)0xa5, 13).ToArray();
        Assert.Throws<CorruptParquetException>(() =>
            ParquetDecompressor.DecompressInto(block, CompressionKind.Lz4, destination));
    }

    [Test]
    public void RejectsZeroOffsetInShortcutPath()
    {
        // Extra literals and output space allow the decoder's common shortcut.
        byte[] block = [0x13, 1, 0, 0, 0xf0, 5, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20];
        byte[] destination = Enumerable.Repeat((byte)0xa5, 28).ToArray();
        Assert.Throws<CorruptParquetException>(() =>
            ParquetDecompressor.DecompressInto(block, CompressionKind.Lz4, destination));
    }

    [Test]
    public unsafe void BothDecoderImplementationsRejectZeroOffset()
    {
        byte[] block = [0x13, 1, 0, 0, 0x50, 2, 3, 4, 5, 6];
        byte[] destination = new byte[13];
        fixed (byte* input = block)
        fixed (byte* output = destination)
        {
            if (Plank.K4os.LZ4.Engine.LL32.LZ4_decompress_safe(input, output, block.Length, destination.Length) >= 0 ||
                Plank.K4os.LZ4.Engine.LL64.LZ4_decompress_safe(input, output, block.Length, destination.Length) >= 0)
                throw new InvalidOperationException("A decoder accepted a zero match offset.");
        }
    }

    [Test]
    public void VendoredTypesAreNotPublic()
    {
        if (typeof(ParquetDecompressor).Assembly.GetExportedTypes().Any(t =>
                t.Namespace?.StartsWith("Plank.K4os.LZ4", StringComparison.Ordinal) == true ||
                t.Namespace?.StartsWith("K4os.Compression.LZ4", StringComparison.Ordinal) == true))
            throw new InvalidOperationException("The vendored LZ4 implementation leaked a public type.");
    }

    [Test]
    public async Task AllowsZeroLiteralBytes()
    {
        byte[] block = [0xf0, 1, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15];
        var decoded = ParquetDecompressor.Decompress(block, 16, CompressionKind.Lz4);
        await Assert.That(decoded).IsEquivalentTo(new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 });
    }
}
