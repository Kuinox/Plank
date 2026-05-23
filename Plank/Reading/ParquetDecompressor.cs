using System.IO.Compression;
using K4os.Compression.LZ4;
using Plank.Writing;
using ZstdSharp;

namespace Plank.Reading;

static class ParquetDecompressor
{
    internal static byte[] Decompress(ReadOnlySpan<byte> payload, uint expectedLength, CompressionKind compression)
    {
        var buffer = new byte[(int)expectedLength];
        DecompressInto(payload, compression, buffer);
        return buffer;
    }

    internal static void DecompressInto(ReadOnlySpan<byte> payload, CompressionKind compression, Span<byte> destination)
    {
        try
        {
            switch (compression)
            {
                case CompressionKind.Gzip:
                    DecompressGzipInto(payload, destination);
                    break;
                case CompressionKind.Brotli:
                    DecompressBrotliInto(payload, destination);
                    break;
                case CompressionKind.Lz4:
                    LZ4Codec.Decode(payload, destination);
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
            Plank.Snappy.SnappyCodec.Decompress(payload, destination);
        }
        catch (InvalidOperationException ex)
        {
            throw new CorruptParquetException("Snappy decompression failed due to invalid compressed data.", ex);
        }
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

    // Allocates: .NET has no span-based GZip API until .NET 11 (GZipDecoder.TryDecompress, dotnet/runtime#62113).
    static void DecompressGzipInto(ReadOnlySpan<byte> payload, Span<byte> destination)
    {
        using var memory = new MemoryStream(payload.ToArray(), writable: false);
        using var stream = new GZipStream(memory, CompressionMode.Decompress, leaveOpen: true);
        stream.ReadExactly(destination);
    }

    // Allocates: .NET has no built-in Zstd API until .NET 11 (ZstandardDecoder.TryDecompress, dotnet/runtime#59591).
    static void DecompressZstdInto(ReadOnlySpan<byte> payload, Span<byte> destination)
    {
        using var decompressor = new Decompressor();
        try
        {
            decompressor.Unwrap(payload, destination);
        }
        catch (ZstdException ex)
        {
            throw new CorruptParquetException("Zstd decompression failed.", ex);
        }
    }
}
