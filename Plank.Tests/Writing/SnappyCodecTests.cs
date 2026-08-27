using Plank.Snappy;

namespace Plank.Tests.Writing;

internal sealed class SnappyCodecTests
{
    [Test]
    public void CompressAndDecompressRoundTrip()
    {
        var source = new byte[4096];
        for (var i = 0; i < source.Length; i++)
            source[i] = (byte)(i * 31 + 17);

        var compressed = new byte[SnappyCodec.GetMaxCompressedLength(source.Length)];
        var compressedLength = SnappyCodec.Compress(source, compressed);
        var decompressed = new byte[source.Length];
        var decompressedLength = SnappyCodec.Decompress(compressed.AsSpan(0, compressedLength), decompressed);

        if (decompressedLength != source.Length || !decompressed.AsSpan().SequenceEqual(source))
            throw new InvalidOperationException("Snappy round-trip did not reproduce the source payload.");
    }
}
