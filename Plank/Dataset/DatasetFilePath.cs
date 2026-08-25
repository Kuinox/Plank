namespace Plank.Dataset;

/// <summary>Selects the UTF-8 path of a file produced for a dataset partition.</summary>
/// <param name="partitionKey">The partition key selected from a row.</param>
/// <param name="fileIndex">The zero-based index of the file in this dataset write.</param>
/// <param name="bufferPool">The writer buffer pool.</param>
/// <param name="allocation">
/// The optional allocation that owns the returned path. Set it to null when the path has another owner.
/// </param>
/// <returns>The full UTF-8 file path.</returns>
public delegate ReadOnlySpan<byte> DatasetFilePath(ReadOnlySpan<byte> partitionKey, ulong fileIndex,
    IParquetBufferPool bufferPool, out ParquetBuffer? allocation);
