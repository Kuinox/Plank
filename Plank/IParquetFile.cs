using Plank.Reading;

namespace Plank;

/// <summary>Provides reusable random-access file operations for Parquet readers and writers.</summary>
public interface IParquetFile : IParquetReadSource, IDisposable
{
    /// <summary>Opens a file from its full UTF-8 path.</summary>
    /// <param name="path">The full UTF-8 file path.</param>
    /// <param name="mode">The file open mode.</param>
    void Open(ReadOnlySpan<byte> path, FileMode mode);

    /// <summary>Closes the current file and keeps this object available for reuse.</summary>
    void Close();

    /// <summary>Writes bytes at the specified file offset.</summary>
    /// <param name="offset">The zero-based file offset.</param>
    /// <param name="source">The bytes to write.</param>
    void Write(ulong offset, ReadOnlySpan<byte> source);

    /// <summary>Changes the file length.</summary>
    /// <param name="length">The new file length.</param>
    void SetLength(ulong length);

    /// <summary>Flushes pending writes to storage.</summary>
    void Flush();
}
