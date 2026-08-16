using System.Buffers.Binary;
using K4os.Compression.LZ4;

namespace Plank.Reading;

static class Lz4LegacyDecompressor
{
    const uint FrameMagic = 0x184D2204;
    const uint SkippableFrameMagic = 0x184D2A50;
    const uint SkippableFrameMagicMask = 0xFFFFFFF0;
    const int DictionarySize = 64 * 1024;

    internal static int Decompress(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (StartsWithFrame(source))
            return DecompressFrames(source, destination);
        if (TryDecompressHadoop(source, destination, out var written))
            return written;
        return LZ4Codec.Decode(source, destination);
    }

    static int DecompressFrames(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        var sourceOffset = 0;
        var destinationOffset = 0;
        while (sourceOffset < source.Length)
        {
            EnsureAvailable(source, sourceOffset, sizeof(uint), "LZ4 frame magic");
            var magic = BinaryPrimitives.ReadUInt32LittleEndian(source[sourceOffset..]);
            if ((magic & SkippableFrameMagicMask) == SkippableFrameMagic)
            {
                SkipFrame(source, ref sourceOffset);
                continue;
            }
            if (magic != FrameMagic)
                throw new CorruptParquetException("An LZ4 frame sequence contains an invalid frame magic value.");

            var written = DecompressFrame(source[sourceOffset..], destination[destinationOffset..], out var consumed);
            sourceOffset += consumed;
            destinationOffset += written;
        }

        return destinationOffset;
    }

    static int DecompressFrame(ReadOnlySpan<byte> source, Span<byte> destination, out int consumed)
    {
        var offset = sizeof(uint);
        EnsureAvailable(source, offset, 2, "LZ4 frame descriptor");
        var descriptorOffset = offset;
        var flags = source[offset++];
        var blockDescriptor = source[offset++];
        if ((flags & 0xC0) != 0x40)
            throw new CorruptParquetException("The LZ4 frame version is not supported.");
        if ((flags & 0x02) != 0)
            throw new CorruptParquetException("The LZ4 frame descriptor has a reserved flag set.");
        if ((blockDescriptor & 0x8F) != 0)
            throw new CorruptParquetException("The LZ4 frame block descriptor has reserved bits set.");

        var blockMaximum = ((blockDescriptor >> 4) & 0x07) switch
        {
            4 => 64 * 1024,
            5 => 256 * 1024,
            6 => 1024 * 1024,
            7 => 4 * 1024 * 1024,
            _ => throw new CorruptParquetException("The LZ4 frame block maximum size is invalid.")
        };
        var independentBlocks = (flags & 0x20) != 0;
        var blockChecksum = (flags & 0x10) != 0;
        var hasContentSize = (flags & 0x08) != 0;
        var contentChecksum = (flags & 0x04) != 0;
        var hasDictionary = (flags & 0x01) != 0;

        ulong declaredContentSize = 0;
        if (hasContentSize)
        {
            EnsureAvailable(source, offset, sizeof(ulong), "LZ4 frame content size");
            declaredContentSize = BinaryPrimitives.ReadUInt64LittleEndian(source[offset..]);
            offset += sizeof(ulong);
            if (declaredContentSize > (ulong)destination.Length)
                throw new CorruptParquetException("The LZ4 frame content does not fit the destination buffer.");
        }
        if (hasDictionary)
        {
            EnsureAvailable(source, offset, sizeof(uint), "LZ4 frame dictionary identifier");
            offset += sizeof(uint);
        }

        EnsureAvailable(source, offset, 1, "LZ4 frame header checksum");
        var expectedHeaderChecksum = (byte)(XxHash32.Compute(source[descriptorOffset..offset]) >> 8);
        if (source[offset++] != expectedHeaderChecksum)
            throw new CorruptParquetException("The LZ4 frame header checksum is invalid.");
        if (hasDictionary)
            throw new NotSupportedException("LZ4 frames that require an external dictionary are not supported.");

        var written = 0;
        while (true)
        {
            EnsureAvailable(source, offset, sizeof(uint), "LZ4 frame block header");
            var blockHeader = BinaryPrimitives.ReadUInt32LittleEndian(source[offset..]);
            offset += sizeof(uint);
            if (blockHeader == 0)
                break;

            var uncompressed = (blockHeader & 0x80000000) != 0;
            var storedLength = blockHeader & 0x7FFFFFFF;
            if (storedLength == 0 || storedLength > blockMaximum)
                throw new CorruptParquetException("The LZ4 frame block length is invalid.");
            EnsureAvailable(source, offset, checked((int)storedLength), "LZ4 frame block payload");
            var storedBlock = source.Slice(offset, (int)storedLength);
            offset += (int)storedLength;

            if (blockChecksum)
            {
                EnsureAvailable(source, offset, sizeof(uint), "LZ4 frame block checksum");
                var expectedBlockChecksum = BinaryPrimitives.ReadUInt32LittleEndian(source[offset..]);
                offset += sizeof(uint);
                if (XxHash32.Compute(storedBlock) != expectedBlockChecksum)
                    throw new CorruptParquetException("The LZ4 frame block checksum is invalid.");
            }

            if (uncompressed)
            {
                if (storedLength > (uint)(destination.Length - written))
                    throw new CorruptParquetException("The LZ4 frame output exceeds the destination buffer.");
                storedBlock.CopyTo(destination[written..]);
                written += (int)storedLength;
                continue;
            }

            var maximumOutput = Math.Min(blockMaximum, destination.Length - written);
            if (maximumOutput == 0)
                throw new CorruptParquetException("The LZ4 frame output exceeds the destination buffer.");
            var dictionaryLength = independentBlocks ? 0 : Math.Min(written, DictionarySize);
            var dictionary = destination.Slice(written - dictionaryLength, dictionaryLength);
            var blockWritten = DecodeBlock(storedBlock, destination.Slice(written, maximumOutput), dictionary);
            if (blockWritten <= 0)
                throw new CorruptParquetException("An LZ4 frame block could not be decompressed.");
            written += blockWritten;
        }

        if (hasContentSize && (ulong)written != declaredContentSize)
            throw new CorruptParquetException("The LZ4 frame content size does not match its decoded length.");
        if (contentChecksum)
        {
            EnsureAvailable(source, offset, sizeof(uint), "LZ4 frame content checksum");
            var expectedContentChecksum = BinaryPrimitives.ReadUInt32LittleEndian(source[offset..]);
            offset += sizeof(uint);
            if (XxHash32.Compute(destination[..written]) != expectedContentChecksum)
                throw new CorruptParquetException("The LZ4 frame content checksum is invalid.");
        }

        consumed = offset;
        return written;
    }

    static bool TryDecompressHadoop(ReadOnlySpan<byte> source, Span<byte> destination, out int written)
    {
        written = 0;
        if (!TryReadHadoopBlock(source, destination, out var sourceOffset, out written))
            return false;

        while (sourceOffset < source.Length)
        {
            EnsureAvailable(source, sourceOffset, sizeof(uint) * 2, "Hadoop LZ4 block header");
            var uncompressedLength = BinaryPrimitives.ReadUInt32BigEndian(source[sourceOffset..]);
            var storedLength = BinaryPrimitives.ReadUInt32BigEndian(source[(sourceOffset + sizeof(uint))..]);
            sourceOffset += sizeof(uint) * 2;
            if (uncompressedLength == 0 || storedLength == 0 ||
                uncompressedLength > (uint)(destination.Length - written) ||
                storedLength > (uint)(source.Length - sourceOffset))
                throw new CorruptParquetException("A Hadoop LZ4 block has invalid lengths.");

            var blockWritten = DecodeBlock(source.Slice(sourceOffset, (int)storedLength),
                destination.Slice(written, (int)uncompressedLength), default);
            if (blockWritten != (int)uncompressedLength)
                throw new CorruptParquetException("A Hadoop LZ4 block decoded to an unexpected length.");
            sourceOffset += (int)storedLength;
            written += blockWritten;
        }

        return true;
    }

    static bool TryReadHadoopBlock(ReadOnlySpan<byte> source, Span<byte> destination, out int consumed, out int written)
    {
        consumed = 0;
        written = 0;
        if (source.Length < sizeof(uint) * 2)
            return false;

        var uncompressedLength = BinaryPrimitives.ReadUInt32BigEndian(source);
        var storedLength = BinaryPrimitives.ReadUInt32BigEndian(source[sizeof(uint)..]);
        if (uncompressedLength == 0 || storedLength == 0 || uncompressedLength > (uint)destination.Length ||
            storedLength > (uint)(source.Length - sizeof(uint) * 2))
            return false;

        try
        {
            written = LZ4Codec.Decode(source.Slice(sizeof(uint) * 2, (int)storedLength),
                destination[..(int)uncompressedLength]);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            written = 0;
            return false;
        }
        if (written != (int)uncompressedLength)
        {
            written = 0;
            return false;
        }

        consumed = sizeof(uint) * 2 + (int)storedLength;
        return true;
    }

    static int DecodeBlock(ReadOnlySpan<byte> source, Span<byte> destination, ReadOnlySpan<byte> dictionary)
    {
        try
        {
            return dictionary.IsEmpty
                ? LZ4Codec.Decode(source, destination)
                : LZ4Codec.Decode(source, destination, dictionary);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            throw new CorruptParquetException("An LZ4 block could not be decompressed.", exception);
        }
    }

    static bool StartsWithFrame(ReadOnlySpan<byte> source)
    {
        if (source.Length < sizeof(uint))
            return false;
        var magic = BinaryPrimitives.ReadUInt32LittleEndian(source);
        return magic == FrameMagic || (magic & SkippableFrameMagicMask) == SkippableFrameMagic;
    }

    static void SkipFrame(ReadOnlySpan<byte> source, ref int offset)
    {
        EnsureAvailable(source, offset, sizeof(uint) * 2, "skippable LZ4 frame header");
        var length = BinaryPrimitives.ReadUInt32LittleEndian(source[(offset + sizeof(uint))..]);
        offset += sizeof(uint) * 2;
        if (length > (uint)(source.Length - offset))
            throw new CorruptParquetException("A skippable LZ4 frame is truncated.");
        offset += (int)length;
    }

    static void EnsureAvailable(ReadOnlySpan<byte> source, int offset, int length, string component)
    {
        if (length > source.Length || offset > source.Length - length)
            throw new CorruptParquetException($"The {component} is truncated.");
    }
}
