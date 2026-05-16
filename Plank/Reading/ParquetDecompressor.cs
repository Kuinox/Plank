using System.IO.Compression;
using K4os.Compression.LZ4;
using Plank.Writing;
using ZstdSharp;

namespace Plank.Reading;

static class ParquetDecompressor
{
    internal static byte[] Decompress(ReadOnlySpan<byte> payload, uint expectedLength, CompressionKind compression)
    {
        byte[] result;
        try
        {
            result = compression switch
            {
                CompressionKind.Gzip => DecompressWithStream(payload, expectedLength,
                    static s => new GZipStream(s, CompressionMode.Decompress, leaveOpen: true)),
                CompressionKind.Brotli => DecompressWithStream(payload, expectedLength,
                    static s => new BrotliStream(s, CompressionMode.Decompress, leaveOpen: true)),
                CompressionKind.Lz4 => DecompressLz4(payload, expectedLength),
                CompressionKind.Zstd => DecompressZstd(payload, expectedLength),
                CompressionKind.Snappy => DecompressSnappy(payload, expectedLength),
                _ => throw new NotSupportedException($"Compression '{compression}' is not supported.")
            };
        }
        catch (InvalidDataException ex)
        {
            throw new CorruptParquetException($"{compression} decompression failed due to invalid compressed data.", ex);
        }

        if ((uint)result.Length != expectedLength)
            throw new CorruptParquetException(
                $"{compression} decompression produced {result.Length} bytes but {expectedLength} were expected.");

        return result;
    }

    static byte[] DecompressWithStream(ReadOnlySpan<byte> payload, uint expectedLength, Func<MemoryStream, Stream> create)
    {
        using var memory = new MemoryStream(payload.ToArray(), writable: false);
        using var stream = create(memory);
        var buffer = new byte[(int)expectedLength];
        stream.ReadExactly(buffer);
        return buffer;
    }

    static byte[] DecompressLz4(ReadOnlySpan<byte> payload, uint expectedLength)
    {
        var buffer = new byte[(int)expectedLength];
        LZ4Codec.Decode(payload, buffer);
        return buffer;
    }

    static byte[] DecompressZstd(ReadOnlySpan<byte> payload, uint expectedLength)
    {
        using var decompressor = new Decompressor();
        var buffer = new byte[(int)expectedLength];
        try
        {
            decompressor.Unwrap(payload, buffer);
        }
        catch (ZstdException ex)
        {
            throw new CorruptParquetException("Zstd decompression failed.", ex);
        }
        return buffer;
    }

    static byte[] DecompressSnappy(ReadOnlySpan<byte> payload, uint expectedLength)
    {
        var buffer = new byte[(int)expectedLength];
        Plank.Snappy.SnappyCodec.Decompress(payload, buffer);
        return buffer;
    }
}
