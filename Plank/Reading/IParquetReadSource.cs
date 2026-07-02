namespace Plank.Reading;

/// <summary>
/// Provides random-access reads over a Parquet data source so callers can read many files without allocating <see cref="FileStream"/> instances.
/// </summary>
public interface IParquetReadSource
{
    /// <summary>
    /// Gets the source length in bytes.
    /// </summary>
    ulong Length { get; }

    /// <summary>
    /// Reads bytes from the source at the specified offset until the destination is filled.
    /// </summary>
    /// <param name="offset">The zero-based source offset to read from.</param>
    /// <param name="destination">The destination buffer to fill.</param>
    void ReadExactly(ulong offset, Span<byte> destination);
}
