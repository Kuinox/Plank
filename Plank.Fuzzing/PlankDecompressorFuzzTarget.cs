using System.Buffers.Binary;
using Plank.Reading;
using Plank.Schema;

namespace Plank.Fuzzing;

/// <summary>
/// Feeds a raw payload straight to one of Plank's decompressors.
/// </summary>
/// <remarks>
/// Every other reader path reaches a decompressor through a Parquet file, which
/// means the corpus can only contain codecs the *writer* can produce. It cannot
/// produce Lz4Legacy at all, so the deprecated-LZ4 reader — a hand-rolled parser
/// for three different framings, with its own length arithmetic — had no input
/// that reached past its first validity check: Lz4LegacyDecompressor sat at
/// 82/326 lines. (Its checksums were hand-written too, at 0/66 lines covered;
/// they are System.IO.Hashing's job now.)
///
/// Fed directly, there is no envelope to satisfy: the fuzzer mutates the
/// compressed bytes and nothing else, which is exactly the shape of input these
/// parsers have to survive. They are also the highest-risk code in the reader —
/// pure offset and length juggling over attacker-controlled bytes.
///
/// Layout of an input:
///   [0]     codec ordinal (mod the codec count)
///   [1..3]  destination length, big-endian, clamped
///   [3..]   the compressed payload
/// </remarks>
public static class PlankDecompressorFuzzTarget
{
    // A decompressor is told how many bytes to produce; the reader gets that from
    // the page header. Capping it keeps a single input from asking for a
    // gigabyte of zeroed memory, which would starve the fuzzer without testing
    // anything the smaller sizes do not.
    const int MaxDestinationLength = 256 * 1024;

    static readonly CompressionKind[] Codecs =
    [
        CompressionKind.Snappy,
        CompressionKind.Gzip,
        CompressionKind.Zstd,
        CompressionKind.Lz4,
        CompressionKind.Brotli,
        CompressionKind.Lz4Legacy
    ];

    public static void Execute(ReadOnlySpan<byte> data)
    {
        if (data.Length < 3)
            return;

        var codec = Codecs[data[0] % Codecs.Length];
        var requested = BinaryPrimitives.ReadUInt16BigEndian(data[1..3]);
        var payload = data[3..];

        // A zero-length destination is a real case the reader can present, so it
        // stays reachable rather than being clamped away.
        var destination = new byte[Math.Min(requested, MaxDestinationLength)];

        try
        {
            ParquetDecompressor.DecompressInto(payload, codec, destination);
        }
        catch (Exception ex) when (ex is CorruptParquetException or NotSupportedException
            or InvalidOperationException)
        {
            // What a malformed frame is supposed to produce. Anything else —
            // IndexOutOfRange, an unhandled overflow, an access violation — is a
            // finding, and reaches the harness uncaught.
        }
    }
}
