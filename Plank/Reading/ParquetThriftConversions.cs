using Plank.Schema;

namespace Plank.Reading;

static class ParquetThriftConversions
{
    internal static CompressionKind ReadCompression(int compression)
        => compression switch
        {
            0 => CompressionKind.None,
            1 => CompressionKind.Snappy,
            2 => CompressionKind.Gzip,
            4 => CompressionKind.Brotli,
            5 => CompressionKind.Lz4Legacy,
            6 => CompressionKind.Zstd,
            7 => CompressionKind.Lz4,
            _ => throw new NotSupportedException($"Compression codec '{compression}' is not supported.")
        };

    // EncodingKind carries the Parquet wire values, so reading is a range check plus a cast.
    internal static EncodingKind ReadEncoding(int encoding)
        => encoding is 0 or (>= 2 and <= 10)
            ? (EncodingKind)encoding
            : throw new NotSupportedException($"Encoding '{encoding}' is not supported.");
}
