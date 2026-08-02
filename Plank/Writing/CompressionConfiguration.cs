using Plank.Schema;
using ZstdSharp;

namespace Plank.Writing;

static class CompressionConfiguration
{
    internal static int ResolveLevel(CompressionKind compression, int? compressionLevel,
        string compressionParameterName, string compressionLevelParameterName)
    {
        if (!Enum.IsDefined(compression))
            throw new ArgumentOutOfRangeException(compressionParameterName, compression,
                "Compression must be a defined CompressionKind value.");

        if (compressionLevel is { } level && !IsValidLevel(compression, level))
            throw new ArgumentOutOfRangeException(compressionLevelParameterName, level,
                $"Compression level '{level}' is not supported for '{compression}'.");

        return compressionLevel ?? compression switch
        {
            CompressionKind.Gzip => 1,
            CompressionKind.Zstd => 1,
            CompressionKind.Lz4 => 0,
            CompressionKind.Brotli => 4,
            _ => 0
        };
    }

    static bool IsValidLevel(CompressionKind compression, int level)
        => compression switch
        {
            CompressionKind.Gzip => level is >= 0 and <= 9,
            CompressionKind.Zstd => level >= Compressor.MinCompressionLevel && level <= Compressor.MaxCompressionLevel,
            CompressionKind.Lz4 => level is 0 or >= 3 and <= 12,
            CompressionKind.Brotli => level is >= 0 and <= 11,
            _ => false
        };
}
