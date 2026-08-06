namespace Plank.Writing;

/// <summary>Provides reusable random-access writes to a Parquet data source.</summary>
public interface IParquetWriteSource
{
    /// <summary>Opens a destination from its full UTF-8 path.</summary>
    /// <param name="path">The full UTF-8 file path.</param>
    /// <param name="mode">The file open mode.</param>
    void Open(ReadOnlySpan<byte> path, FileMode mode);

    /// <summary>Closes the current destination and keeps this object available for reuse.</summary>
    void Close();

    /// <summary>Writes bytes at the specified destination offset.</summary>
    /// <param name="offset">The zero-based destination offset.</param>
    /// <param name="source">The bytes to write.</param>
    void Write(ulong offset, ReadOnlySpan<byte> source);

    /// <summary>Changes the destination length.</summary>
    /// <param name="length">The new destination length.</param>
    void SetLength(ulong length);

    /// <summary>Flushes pending writes to the destination.</summary>
    void Flush();
}
