using System.Buffers.Binary;
using System.IO.Compression;
using System.IO.Hashing;
using K4os.Compression.LZ4;
using Plank.Reading;

namespace Plank.Fuzzing;

/// <summary>
/// Builds seeds for <see cref="PlankDecompressorFuzzTarget"/>: one valid
/// compressed payload per codec and per framing.
/// </summary>
/// <remarks>
/// A decompressor only gets interesting once the fuzzer is *inside* the format.
/// Mutation will not invent a valid LZ4 frame header — four magic bytes, a
/// version field, reserved bits that must be zero, and an XxHash32 header
/// checksum that has to match — so without a seed that already satisfies all of
/// them the block loop, the checksum branches and the frame-chaining code are
/// unreachable. That is exactly the state the LZ4 legacy reader was in.
///
/// The frames here are built by hand rather than by a library so every optional
/// field can be turned on independently: content size, block checksums, content
/// checksum, linked blocks, and the skippable-frame prefix are each their own
/// branch in the parser.
/// </remarks>
static class DecompressorCorpus
{
    const uint FrameMagic = 0x184D2204;
    const uint SkippableFrameMagic = 0x184D2A50;

    // Small and compressible: LZ4 stores an incompressible block verbatim, which
    // takes the "uncompressed block" branch instead of the decode one. Both are
    // wanted, so one payload of each kind appears below.
    static readonly byte[] Compressible =
        [.. System.Text.Encoding.ASCII.GetBytes(new string('a', 200) + new string('b', 200))];

    internal static IEnumerable<(string Name, byte[] Bytes)> BuildCases()
    {
        foreach (var (name, codec, payload, decompressedLength) in Payloads())
        {
            // The target reads a codec ordinal, a destination length and then the
            // payload, so a seed has to carry that prefix to be a complete case.
            // The length has to be the true decompressed size: the decompressors
            // are told how many bytes to produce and reject a mismatch, so a
            // wrong one would stop every seed just short of finishing.
            var seed = new byte[3 + payload.Length];
            seed[0] = codec;
            BinaryPrimitives.WriteUInt16BigEndian(seed.AsSpan(1, 2), (ushort)decompressedLength);
            payload.CopyTo(seed, 3);
            yield return (name, seed);
        }
    }

    // Codec ordinals are positions in PlankDecompressorFuzzTarget's table.
    const byte Snappy = 0;
    const byte Gzip = 1;
    const byte Zstd = 2;
    const byte Lz4Raw = 3;
    const byte Brotli = 4;
    const byte Lz4Legacy = 5;

    static IEnumerable<(string Name, byte Codec, byte[] Payload, int DecompressedLength)> Payloads()
    {
        var n = Compressible.Length;
        yield return ("gzip", Gzip, Deflate(Compressible), n);
        yield return ("brotli", Brotli, BrotliCompress(Compressible), n);
        yield return ("lz4raw", Lz4Raw, Lz4Block(Compressible), n);

        // Lz4Legacy is the interesting one: three separate framings behind one
        // codec value, and the reader picks between them by sniffing the bytes.
        yield return ("lz4legacy-raw", Lz4Legacy, Lz4Block(Compressible), n);
        yield return ("lz4legacy-hadoop", Lz4Legacy, HadoopFrame(Compressible), n);
        yield return ("lz4legacy-hadoop-multiblock", Lz4Legacy, HadoopFrame(Compressible, splitInto: 2), n);

        // One frame per optional-field combination, because each is its own
        // branch through the descriptor parser and the block loop.
        yield return ("lz4legacy-frame", Lz4Legacy, Lz4Frame(Compressible), n);
        yield return ("lz4legacy-frame-contentsize", Lz4Legacy, Lz4Frame(Compressible, contentSize: true), n);
        yield return ("lz4legacy-frame-blockcrc", Lz4Legacy, Lz4Frame(Compressible, blockChecksum: true), n);
        yield return ("lz4legacy-frame-contentcrc", Lz4Legacy, Lz4Frame(Compressible, contentChecksum: true), n);
        yield return ("lz4legacy-frame-linked", Lz4Legacy, Lz4Frame(Compressible, independentBlocks: false), n);
        yield return ("lz4legacy-frame-all", Lz4Legacy,
            Lz4Frame(Compressible, contentSize: true, blockChecksum: true, contentChecksum: true), n);
        yield return ("lz4legacy-frame-multiblock", Lz4Legacy, Lz4Frame(Compressible, splitInto: 3), n);

        // An incompressible payload makes the encoder give up and store the block
        // verbatim, which sets the block header's high bit.
        yield return ("lz4legacy-frame-stored", Lz4Legacy,
            Lz4Frame(Incompressible(64), storeUncompressed: true), 64);

        // A skippable frame in front of a real one: the parser has to hop over it
        // by its declared length and keep going.
        yield return ("lz4legacy-frame-skippable", Lz4Legacy,
            [.. SkippableFrame(8), .. Lz4Frame(Compressible)], n);

        // Two frames back to back exercise the outer chaining loop.
        yield return ("lz4legacy-frame-chained", Lz4Legacy,
            [.. Lz4Frame(Compressible.AsSpan(..200).ToArray()),
             .. Lz4Frame(Compressible.AsSpan(200..).ToArray())], n);
    }

    static byte[] Incompressible(int length)
    {
        // A fixed pattern rather than random bytes: a seed corpus has to be
        // reproducible, or two machines disagree about what they are fuzzing.
        var bytes = new byte[length];
        var state = 0x12345678u;
        for (var i = 0; i < bytes.Length; i++)
        {
            state = (state * 1664525u) + 1013904223u;
            bytes[i] = (byte)(state >> 24);
        }

        return bytes;
    }

    static byte[] Deflate(byte[] source)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
            gzip.Write(source);
        return output.ToArray();
    }

    static byte[] BrotliCompress(byte[] source)
    {
        var buffer = new byte[BrotliEncoder.GetMaxCompressedLength(source.Length)];
        if (!BrotliEncoder.TryCompress(source, buffer, out var written))
            throw new InvalidOperationException("Brotli compression of a seed failed.");
        return buffer[..written];
    }

    static byte[] Lz4Block(ReadOnlySpan<byte> source)
    {
        var buffer = new byte[LZ4Codec.MaximumOutputSize(source.Length)];
        var written = LZ4Codec.Encode(source, buffer);
        if (written <= 0)
            throw new InvalidOperationException("LZ4 compression of a seed failed.");
        return buffer[..written];
    }

    // The Hadoop framing parquet-mr wrote for the deprecated LZ4 codec: pairs of
    // big-endian lengths, uncompressed then stored, one pair per block.
    static byte[] HadoopFrame(byte[] source, int splitInto = 1)
    {
        var output = new MemoryStream();
        var chunk = (source.Length + splitInto - 1) / splitInto;
        for (var offset = 0; offset < source.Length; offset += chunk)
        {
            var slice = source.AsSpan(offset, Math.Min(chunk, source.Length - offset));
            var stored = Lz4Block(slice);
            var header = new byte[8];
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0, 4), (uint)slice.Length);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4, 4), (uint)stored.Length);
            output.Write(header);
            output.Write(stored);
        }

        return output.ToArray();
    }

    static byte[] SkippableFrame(int length)
    {
        var frame = new byte[8 + length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(0, 4), SkippableFrameMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(4, 4), (uint)length);
        return frame;
    }

    // A real LZ4 frame, per the frame format spec, with each optional field
    // switchable so every descriptor branch gets a seed that reaches it.
    static byte[] Lz4Frame(byte[] source, bool contentSize = false, bool blockChecksum = false,
        bool contentChecksum = false, bool independentBlocks = true, bool storeUncompressed = false,
        int splitInto = 1)
    {
        var output = new MemoryStream();
        var magic = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(magic, FrameMagic);
        output.Write(magic);

        // FLG: version 01 in the top two bits, then the option flags.
        var flags = (byte)0x40;
        if (independentBlocks) flags |= 0x20;
        if (blockChecksum) flags |= 0x10;
        if (contentSize) flags |= 0x08;
        if (contentChecksum) flags |= 0x04;

        // BD: block maximum 4 (64 KiB) in bits 4-6. Everything else is reserved
        // and must be zero, which the reader checks.
        const byte BlockDescriptor = 0x40;

        var descriptor = new MemoryStream();
        descriptor.WriteByte(flags);
        descriptor.WriteByte(BlockDescriptor);
        if (contentSize)
        {
            var size = new byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(size, (ulong)source.Length);
            descriptor.Write(size);
        }

        var descriptorBytes = descriptor.ToArray();
        output.Write(descriptorBytes);
        // The header checksum is the second byte of the descriptor's XxHash32,
        // which is why a mutated frame practically never gets past the header.
        output.WriteByte((byte)(XxHash32.HashToUInt32(descriptorBytes) >> 8));

        var chunk = (source.Length + splitInto - 1) / splitInto;
        for (var offset = 0; offset < source.Length; offset += chunk)
        {
            var slice = source.AsSpan(offset, Math.Min(chunk, source.Length - offset));
            byte[] stored;
            bool uncompressed;
            if (storeUncompressed)
            {
                stored = slice.ToArray();
                uncompressed = true;
            }
            else
            {
                stored = Lz4Block(slice);
                // The encoder can produce more bytes than it was given; the frame
                // format says to store the block verbatim when that happens.
                uncompressed = stored.Length >= slice.Length;
                if (uncompressed)
                    stored = slice.ToArray();
            }

            var header = new byte[4];
            var value = (uint)stored.Length;
            if (uncompressed)
                value |= 0x80000000;
            BinaryPrimitives.WriteUInt32LittleEndian(header, value);
            output.Write(header);
            output.Write(stored);
            if (blockChecksum)
            {
                var checksum = new byte[4];
                BinaryPrimitives.WriteUInt32LittleEndian(checksum, XxHash32.HashToUInt32(stored));
                output.Write(checksum);
            }
        }

        output.Write(new byte[4]);   // end mark
        if (contentChecksum)
        {
            var checksum = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(checksum, XxHash32.HashToUInt32(source));
            output.Write(checksum);
        }

        return output.ToArray();
    }
}
