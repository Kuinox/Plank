using Plank.Schema;

namespace Plank.Reading.Physical.Internal;

static class BloomFilterReader
{
    const int HeaderReadSize = 256;

    internal static ParquetBloomFilter Open(ParquetFileReader reader, ParquetColumnChunkInfo chunk)
    {
        if (!chunk.HasBloomFilter)
            throw new InvalidOperationException(
                $"Column {chunk.ColumnOrdinal} in row group {chunk.RowGroupOrdinal} does not have a Bloom filter.");

        var source = reader.Source;
        ValidateRange(chunk.BloomFilterOffset, 1, source.Length);
        return chunk.BloomFilterLength == 0
            ? OpenWithoutLength(reader, chunk)
            : OpenKnownLength(reader, chunk);
    }

    static ParquetBloomFilter OpenKnownLength(ParquetFileReader reader, ParquetColumnChunkInfo chunk)
    {
        var serializedLength = chunk.BloomFilterLength;
        var maximumSerializedLength = checked(ParquetBloomFilterOptions.MaximumSupportedBytes + HeaderReadSize);
        if (serializedLength > maximumSerializedLength)
            throw new CorruptParquetException(
                $"Bloom-filter length {serializedLength} exceeds the supported maximum of {maximumSerializedLength} bytes.");
        ValidateRange(chunk.BloomFilterOffset, serializedLength, reader.Source.Length);

        var payload = reader.BufferPool.Rent(serializedLength);
        try
        {
            var bytes = payload.Span[..checked((int)serializedLength)];
            reader.Source.ReadExactly(chunk.BloomFilterOffset, bytes);
            var headerLength = ReadHeader(bytes, out var bitsetLength);
            if (headerLength + bitsetLength != bytes.Length)
                throw new CorruptParquetException(
                    $"Bloom-filter metadata length {bytes.Length} does not match its header and {bitsetLength}-byte bitset.");

            var bitset = payload.RetainSlice(headerLength, bitsetLength);
            payload.Dispose();
            return new ParquetBloomFilter(bitset);
        }
        catch
        {
            payload.Dispose();
            throw;
        }
    }

    static ParquetBloomFilter OpenWithoutLength(ParquetFileReader reader, ParquetColumnChunkInfo chunk)
    {
        var available = reader.Source.Length - chunk.BloomFilterOffset;
        var prefixLength = checked((int)Math.Min((ulong)HeaderReadSize, available));
        Span<byte> prefix = stackalloc byte[HeaderReadSize];
        reader.Source.ReadExactly(chunk.BloomFilterOffset, prefix[..prefixLength]);
        var headerLength = ReadHeader(prefix[..prefixLength], out var bitsetLength);
        ValidateRange(chunk.BloomFilterOffset + checked((ulong)headerLength), checked((uint)bitsetLength),
            reader.Source.Length);

        var bitset = reader.BufferPool.Rent(checked((uint)bitsetLength));
        try
        {
            reader.Source.ReadExactly(chunk.BloomFilterOffset + checked((ulong)headerLength),
                bitset.Span[..bitsetLength]);
            return new ParquetBloomFilter(bitset.RetainSlice(0, bitsetLength));
        }
        finally
        {
            bitset.Dispose();
        }
    }

    static int ReadHeader(ReadOnlySpan<byte> payload, out int bitsetLength)
    {
        var reader = new CompactProtocolReader(payload);
        var hasLength = false;
        var hasAlgorithm = false;
        var hasHash = false;
        var hasCompression = false;
        bitsetLength = 0;
        reader.BeginRootStruct();
        while (reader.TryReadFieldHeader(out var fieldId, out var type, out var inlineBool))
        {
            switch (fieldId)
            {
                case 1:
                    ThrowIfDuplicate(hasLength, "numBytes");
                    bitsetLength = checked((int)reader.ReadI32AsU32(type, ParquetBloomFilterOptions.MaximumSupportedBytes));
                    hasLength = true;
                    break;
                case 2:
                    ThrowIfDuplicate(hasAlgorithm, "algorithm");
                    ReadSupportedUnion(ref reader, type, "split-block algorithm");
                    hasAlgorithm = true;
                    break;
                case 3:
                    ThrowIfDuplicate(hasHash, "hash");
                    ReadSupportedUnion(ref reader, type, "XXH64 hash");
                    hasHash = true;
                    break;
                case 4:
                    ThrowIfDuplicate(hasCompression, "compression");
                    ReadSupportedUnion(ref reader, type, "uncompressed representation");
                    hasCompression = true;
                    break;
                default:
                    reader.Skip(type, inlineBool);
                    break;
            }
        }

        if (!hasLength || !hasAlgorithm || !hasHash || !hasCompression)
            throw new CorruptParquetException("Bloom-filter header is missing a required field.");
        if (bitsetLength < ParquetBloomFilterOptions.MinimumBytes ||
            bitsetLength % ParquetBloomFilterOptions.MinimumBytes != 0)
            throw new CorruptParquetException(
                $"Bloom-filter bitset size {bitsetLength} must contain one or more {ParquetBloomFilterOptions.MinimumBytes}-byte blocks.");
        return reader.Offset;
    }

    static void ReadSupportedUnion(ref CompactProtocolReader reader, CompactProtocolType wireType, string description)
    {
        var supported = false;
        var fieldCount = 0;
        reader.BeginStruct(wireType);
        while (reader.TryReadFieldHeader(out var fieldId, out var type, out var inlineBool))
        {
            fieldCount++;
            if (fieldId == 1)
            {
                if (supported)
                    throw new CorruptParquetException($"Bloom-filter {description} is declared more than once.");
                ReadEmptyMarker(ref reader, type, description);
                supported = true;
                continue;
            }

            reader.Skip(type, inlineBool);
        }

        if (!supported)
            throw new NotSupportedException($"The Bloom filter does not use the supported {description}.");
        if (fieldCount != 1)
            throw new CorruptParquetException($"Bloom-filter {description} union contains multiple alternatives.");
    }

    static void ReadEmptyMarker(ref CompactProtocolReader reader, CompactProtocolType wireType, string description)
    {
        reader.BeginStruct(wireType);
        if (!reader.TryReadFieldHeader(out _, out var type, out var inlineBool))
            return;

        reader.Skip(type, inlineBool);
        while (reader.TryReadFieldHeader(out _, out type, out inlineBool))
            reader.Skip(type, inlineBool);
        throw new CorruptParquetException($"Bloom-filter {description} marker must be empty.");
    }

    static void ThrowIfDuplicate(bool present, string field)
    {
        if (present)
            throw new CorruptParquetException($"Bloom-filter header field '{field}' is declared more than once.");
    }

    static void ValidateRange(ulong offset, uint length, ulong sourceLength)
    {
        if (offset > sourceLength || length > sourceLength - offset)
            throw new CorruptParquetException(
                $"Bloom-filter range at offset {offset} with length {length} exceeds source length {sourceLength}.");
    }
}
