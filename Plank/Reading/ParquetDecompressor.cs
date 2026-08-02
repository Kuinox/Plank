using System.IO.Compression;
using K4os.Compression.LZ4;
using Plank.Schema;
using ZstdSharp.Unsafe;

namespace Plank.Reading;

static class ParquetDecompressor
{
    internal static byte[] Decompress(ReadOnlySpan<byte> payload, uint expectedLength, CompressionKind compression)
    {
        var buffer = new byte[(int)expectedLength];
        DecompressInto(payload, compression, buffer);
        return buffer;
    }

    internal static void DecompressInto(ReadOnlySpan<byte> payload, CompressionKind compression,
        Span<byte> destination)
    {
        try
        {
            var written = compression switch
            {
                CompressionKind.Gzip => GzipInflater.Decompress(payload, destination),
                CompressionKind.Brotli => DecompressBrotliInto(payload, destination),
                CompressionKind.Lz4 => DecompressLz4Into(payload, destination),
                CompressionKind.Zstd => DecompressZstdInto(payload, destination),
                CompressionKind.Snappy => DecompressSnappyInto(payload, destination),
                _ => throw new NotSupportedException($"Compression '{compression}' is not supported.")
            };

            if (written != destination.Length)
                throw new CorruptParquetException(
                    $"{compression} decompression produced {written} bytes but {destination.Length} were expected.");
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or EndOfStreamException)
        {
            throw new CorruptParquetException($"{compression} decompression failed due to invalid compressed data.", ex);
        }
    }

    static int DecompressSnappyInto(ReadOnlySpan<byte> payload, Span<byte> destination)
    {
        try
        {
            return Plank.Snappy.SnappyCodec.Decompress(payload, destination);
        }
        catch (InvalidOperationException ex)
        {
            throw new CorruptParquetException("Snappy decompression failed due to invalid compressed data.", ex);
        }
    }

    static int DecompressLz4Into(ReadOnlySpan<byte> payload, Span<byte> destination) =>
        LZ4Codec.Decode(payload, destination);

    static int DecompressBrotliInto(ReadOnlySpan<byte> payload, Span<byte> destination)
    {
        try
        {
            if (!BrotliDecoder.TryDecompress(payload, destination, out var written))
                throw new CorruptParquetException("Brotli decompression failed due to invalid compressed data.");
            return written;
        }
        catch (InvalidOperationException ex)
        {
            throw new CorruptParquetException("Brotli decompression failed due to invalid compressed data.", ex);
        }
    }

    static unsafe int DecompressZstdInto(ReadOnlySpan<byte> payload, Span<byte> destination)
    {
        fixed (byte* source = payload)
        fixed (byte* output = destination)
        {
            var written = Methods.ZSTD_decompress(output, (nuint)destination.Length, source, (nuint)payload.Length);
            if (Methods.ZSTD_isError(written))
                throw new CorruptParquetException($"Zstd decompression failed with code {written}.");
            return checked((int)written);
        }
    }
}
