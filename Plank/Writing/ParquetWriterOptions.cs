namespace Plank.Writing;

public sealed class ParquetWriterOptions
{
    public static readonly ParquetWriterOptions Default = new();

    public IParquetBufferPool BufferPool { get; init; } = DefaultParquetBufferPool.Shared;

    public uint BufferChunkSizeBytes { get; init; } = 64 * 1024;

    public uint InitialPageBufferBytes { get; init; } = 320 * 1024;

    public uint InitialColumnBufferBytes { get; init; } = 40 * 1024 * 1024;

    public uint InitialPageCapacity { get; init; } = 4;

    public uint TargetDataPageSizeBytes { get; init; } = 1024 * 1024;

    public CompressionKind Compression { get; init; } = CompressionKind.None;

    public bool WritePageIndexes { get; init; } = true;

    public uint RowApiMaxParallelism { get; init; } = checked((uint)Environment.ProcessorCount);

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(BufferPool);
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
        if (RowApiMaxParallelism == 0)
            throw new ArgumentOutOfRangeException(nameof(RowApiMaxParallelism), RowApiMaxParallelism,
                "Row API max parallelism must be greater than zero.");
        if (!Enum.IsDefined(Compression))
            throw new ArgumentOutOfRangeException(nameof(Compression), Compression,
                "Compression must be a defined CompressionKind value.");
    }
}
