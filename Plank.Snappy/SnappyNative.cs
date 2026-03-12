using System.Runtime.InteropServices;

namespace Plank.Snappy;

static partial class SnappyNative
{
    internal const string LibraryName = "plank_snappy_native";

    [LibraryImport(LibraryName, EntryPoint = "snappy_max_compressed_length")]
    internal static partial nuint MaxCompressedLength(nuint sourceLength);

    [LibraryImport(LibraryName, EntryPoint = "snappy_compress")]
    internal static unsafe partial SnappyStatus Compress(byte* input, nuint inputLength, byte* compressed, ref nuint compressedLength);

    [LibraryImport(LibraryName, EntryPoint = "snappy_uncompressed_length")]
    internal static unsafe partial SnappyStatus GetUncompressedLength(byte* compressed, nuint compressedLength,
        ref nuint result);

    [LibraryImport(LibraryName, EntryPoint = "snappy_uncompress")]
    internal static unsafe partial SnappyStatus Uncompress(byte* compressed, nuint compressedLength, byte* uncompressed,
        ref nuint uncompressedLength);
}
