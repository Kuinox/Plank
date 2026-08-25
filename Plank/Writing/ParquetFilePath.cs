namespace Plank.Writing;

/// <summary>Selects the UTF-8 path of a file produced by a rolling row writer.</summary>
/// <param name="fileIndex">The zero-based index of the file.</param>
/// <param name="bufferPool">The writer buffer pool.</param>
/// <param name="allocation">
/// The optional allocation that owns the returned path. Set it to null when the path has another owner.
/// </param>
/// <returns>The full UTF-8 file path.</returns>
public delegate ReadOnlySpan<byte> ParquetFilePath(ulong fileIndex, IParquetBufferPool bufferPool,
    out ParquetBuffer? allocation);
