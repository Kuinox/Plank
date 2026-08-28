using System.Collections.Immutable;
using Plank.Schema;

namespace Plank.Writing;

public sealed class ParquetWriterOptions
{
    public static readonly ParquetWriterOptions Default = new();

    readonly uint _rowApiMaxParallelism;

    public IParquetBufferPool BufferPool { get; init; } = DefaultParquetBufferPool.Shared;

    public ParquetExecutionOptions Execution { get; init; } = new();

    public uint BufferChunkSizeBytes { get; init; } = 64 * 1024;

    public uint InitialPageBufferBytes { get; init; } = 320 * 1024;

    public uint InitialColumnBufferBytes { get; init; } = 40 * 1024 * 1024;

    public uint InitialPageCapacity { get; init; } = 4;

    /// <summary>Gets or initializes the initial row capacity of each generated row-writer buffer.</summary>
    public int RowApiInitialRowCapacity { get; init; } = 1024;

    public uint TargetDataPageSizeBytes { get; init; } = 1024 * 1024;

    /// <summary>Gets or initializes the target uncompressed size of row groups produced by the row APIs.</summary>
    public ulong TargetRowGroupSizeBytes { get; init; } = 128UL * 1024 * 1024;

    /// <summary>Gets or initializes the target size at which rolling row writers start a new file.</summary>
    public ulong TargetFileSizeBytes { get; init; } = 512UL * 1024 * 1024;

    public ParquetFileVersion FileVersion { get; init; } = ParquetFileVersion.V1;

    public ParquetDataPageVersion DataPageVersion { get; init; } = ParquetDataPageVersion.V2;

    public CompressionKind Compression { get; init; } = CompressionKind.None;

    public int? CompressionLevel { get; init; }

    public string? CreatedBy { get; init; }

    public IReadOnlyList<ParquetKeyValueMetadata> KeyValueMetadata { get; init; } = [];

    public bool WritePageIndexes { get; init; } = true;

    /// <summary>Gets or initializes the lexicographic sort order declared for every row group written to the file.</summary>
    public ImmutableArray<ParquetSortingColumn> SortingColumns { get; init; } = [];

    public bool WritePageCrc { get; init; }

    public uint RowApiMaxParallelism
    {
        get => _rowApiMaxParallelism == 0 ? checked((uint)Execution.WorkerCount) : _rowApiMaxParallelism;
        init => _rowApiMaxParallelism = value;
    }

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(BufferPool);
        ArgumentNullException.ThrowIfNull(Execution);
        Execution.Validate();
        if (BufferChunkSizeBytes == 0)
            throw new ArgumentOutOfRangeException(nameof(BufferChunkSizeBytes), BufferChunkSizeBytes,
                "Buffer chunk size must be greater than zero.");
        if (InitialPageBufferBytes == 0)
            throw new ArgumentOutOfRangeException(nameof(InitialPageBufferBytes), InitialPageBufferBytes,
                "Initial page buffer size must be greater than zero.");
        if (InitialColumnBufferBytes == 0)
            throw new ArgumentOutOfRangeException(nameof(InitialColumnBufferBytes), InitialColumnBufferBytes,
                "Initial column buffer size must be greater than zero.");
        if (TargetDataPageSizeBytes == 0)
            throw new ArgumentOutOfRangeException(nameof(TargetDataPageSizeBytes), TargetDataPageSizeBytes,
                "Target data page size must be greater than zero.");
        if (TargetDataPageSizeBytes > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(TargetDataPageSizeBytes), TargetDataPageSizeBytes,
                $"Target data page size must be <= {int.MaxValue}.");
        if (RowApiInitialRowCapacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(RowApiInitialRowCapacity), RowApiInitialRowCapacity,
                "The row API initial row capacity must be greater than zero.");
        if (TargetRowGroupSizeBytes == 0)
            throw new ArgumentOutOfRangeException(nameof(TargetRowGroupSizeBytes), TargetRowGroupSizeBytes,
                "Target row group size must be greater than zero.");
        if (TargetFileSizeBytes == 0)
            throw new ArgumentOutOfRangeException(nameof(TargetFileSizeBytes), TargetFileSizeBytes,
                "Target file size must be greater than zero.");
        if (SortingColumns.IsDefault)
            throw new ArgumentException("Sorting columns must not be an uninitialized ImmutableArray.",
                nameof(SortingColumns));
        if (!Enum.IsDefined(FileVersion))
            throw new ArgumentOutOfRangeException(nameof(FileVersion), FileVersion,
                "File version must be a defined ParquetFileVersion value.");
        if (!Enum.IsDefined(DataPageVersion))
            throw new ArgumentOutOfRangeException(nameof(DataPageVersion), DataPageVersion,
                "Data page version must be a defined ParquetDataPageVersion value.");
        _ = CompressionConfiguration.ResolveLevel(Compression, CompressionLevel,
            nameof(Compression), nameof(CompressionLevel));
        ArgumentNullException.ThrowIfNull(KeyValueMetadata);
        for (var i = 0; i < KeyValueMetadata.Count; i++)
        {
            var entry = KeyValueMetadata[i];
            if (string.IsNullOrEmpty(entry.Key))
                throw new ArgumentException($"Key-value metadata entry {i} must have a non-empty key.",
                    nameof(KeyValueMetadata));
        }
    }
}
