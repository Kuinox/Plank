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
            switch (compression)
            {
                case CompressionKind.Gzip:
                    GzipInflater.Decompress(payload, destination);
                    break;
                case CompressionKind.Brotli:
                    DecompressBrotliInto(payload, destination);
                    break;
                case CompressionKind.Lz4:
                    DecompressLz4Into(payload, destination);
                    break;
                case CompressionKind.Zstd:
                    DecompressZstdInto(payload, destination);
                    break;
                case CompressionKind.Snappy:
                    DecompressSnappyInto(payload, destination);
                    break;
                default:
                    throw new NotSupportedException($"Compression '{compression}' is not supported.");
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or EndOfStreamException)
        {
            throw new CorruptParquetException($"{compression} decompression failed due to invalid compressed data.", ex);
        }
    }

    static void DecompressSnappyInto(ReadOnlySpan<byte> payload, Span<byte> destination)
    {
        try
        {
            var written = Plank.Snappy.SnappyCodec.Decompress(payload, destination);
            if (written != destination.Length)
                throw new CorruptParquetException(
                    $"Snappy decompression produced {written} bytes but {destination.Length} were expected.");
        }
        catch (InvalidOperationException ex)
        {
            throw new CorruptParquetException("Snappy decompression failed due to invalid compressed data.", ex);
        }
    }

    static void DecompressLz4Into(ReadOnlySpan<byte> payload, Span<byte> destination)
    {
        var written = LZ4Codec.Decode(payload, destination);
        if (written != destination.Length)
            throw new CorruptParquetException(
                $"Lz4 decompression produced {written} bytes but {destination.Length} were expected.");
    }

    static void DecompressBrotliInto(ReadOnlySpan<byte> payload, Span<byte> destination)
    {
        try
        {
            if (!BrotliDecoder.TryDecompress(payload, destination, out var written) || written != destination.Length)
                throw new CorruptParquetException("Brotli decompression failed due to invalid compressed data.");
        }
        catch (InvalidOperationException ex)
        {
            throw new CorruptParquetException("Brotli decompression failed due to invalid compressed data.", ex);
        }
    }

    static unsafe void DecompressZstdInto(ReadOnlySpan<byte> payload, Span<byte> destination)
    {
        fixed (byte* source = payload)
        fixed (byte* output = destination)
        {
            var written = Methods.ZSTD_decompress(output, (nuint)destination.Length, source, (nuint)payload.Length);
            if (Methods.ZSTD_isError(written))
                throw new CorruptParquetException($"Zstd decompression failed with code {written}.");
            if (written != (nuint)destination.Length)
                throw new CorruptParquetException(
                    $"Zstd decompression produced {written} bytes but {destination.Length} were expected.");
        }
    }
}
