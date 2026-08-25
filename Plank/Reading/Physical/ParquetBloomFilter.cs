using Plank.BloomFilters;

namespace Plank.Reading.Physical;

/// <summary>Owns a loaded Parquet split-block Bloom-filter bitset.</summary>
/// <remarks>Dispose the filter to return its storage to the reader's configured buffer pool.</remarks>
public sealed class ParquetBloomFilter : IDisposable
{
    ParquetBuffer _bitset;
    bool _disposed;

    internal ParquetBloomFilter(ParquetBuffer bitset)
        => _bitset = bitset;

    /// <summary>Gets the Bloom-filter bitset size in bytes.</summary>
    public int BitsetSizeBytes
    {
        get
        {
            ThrowIfDisposed();
            return _bitset.Length;
        }
    }

    /// <summary>Gets a view of the uncompressed split-block bitset.</summary>
    public ReadOnlySpan<byte> Bitset
    {
        get
        {
            ThrowIfDisposed();
            return _bitset.Span;
        }
    }

    /// <summary>Checks an INT32 value.</summary>
    public bool MightContain(int value)
        => MightContainHash(ParquetBloomFilterHash.Hash(value));

    /// <summary>Checks an unsigned INT32 logical value.</summary>
    public bool MightContain(uint value)
        => MightContainHash(ParquetBloomFilterHash.Hash(value));

    /// <summary>Checks an INT32-backed unsigned 8-bit logical value.</summary>
    public bool MightContain(byte value)
        => MightContain((int)value);

    /// <summary>Checks an INT32-backed unsigned 16-bit logical value.</summary>
    public bool MightContain(ushort value)
        => MightContain((int)value);

    /// <summary>Checks an INT64 value.</summary>
    public bool MightContain(long value)
        => MightContainHash(ParquetBloomFilterHash.Hash(value));

    /// <summary>Checks an unsigned INT64 logical value.</summary>
    public bool MightContain(ulong value)
        => MightContainHash(ParquetBloomFilterHash.Hash(value));

    /// <summary>Checks a FLOAT value.</summary>
    public bool MightContain(float value)
        => MightContainHash(ParquetBloomFilterHash.Hash(value));

    /// <summary>Checks a DOUBLE value.</summary>
    public bool MightContain(double value)
        => MightContainHash(ParquetBloomFilterHash.Hash(value));

    /// <summary>Checks a BYTE_ARRAY, FIXED_LEN_BYTE_ARRAY, or INT96 value.</summary>
    public bool MightContain(ReadOnlySpan<byte> value)
        => MightContainHash(ParquetBloomFilterHash.Hash(value));

    /// <summary>Checks a UUID logical value using its Parquet big-endian byte representation.</summary>
    public bool MightContain(Guid value)
        => MightContainHash(ParquetBloomFilterHash.Hash(value));

    /// <summary>Checks a caller-computed XXH64 hash.</summary>
    public bool MightContainHash(ulong hash)
    {
        ThrowIfDisposed();
        return SplitBlockBloomFilter.MightContainHash(_bitset.Span, hash);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _bitset.Dispose();
    }

    void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(ParquetBloomFilter));
    }
}
