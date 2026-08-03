using Plank.Schema;
using ZstdSharp;

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

    public uint TargetDataPageSizeBytes { get; init; } = 1024 * 1024;

    public CompressionKind Compression { get; init; } = CompressionKind.None;

    public int? CompressionLevel { get; init; }

    public string? CreatedBy { get; init; }

    public IReadOnlyList<ParquetKeyValueMetadata> KeyValueMetadata { get; init; } = [];

    public bool WritePageIndexes { get; init; } = true;

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
        if (!Enum.IsDefined(Compression))
            throw new ArgumentOutOfRangeException(nameof(Compression), Compression,
                "Compression must be a defined CompressionKind value.");
        ValidateCompressionLevel();
        ArgumentNullException.ThrowIfNull(KeyValueMetadata);
        for (var i = 0; i < KeyValueMetadata.Count; i++)
        {
            var entry = KeyValueMetadata[i];
            if (string.IsNullOrEmpty(entry.Key))
                throw new ArgumentException($"Key-value metadata entry {i} must have a non-empty key.",
                    nameof(KeyValueMetadata));
        }
    }

    internal int GetCompressionLevel()
        => CompressionLevel ?? Compression switch
        {
            CompressionKind.Gzip => 1,
            CompressionKind.Zstd => 1,
            CompressionKind.Lz4 => 0,
            CompressionKind.Brotli => 4,
            _ => 0
        };

    void ValidateCompressionLevel()
    {
        if (CompressionLevel is not { } level)
            return;

        var valid = Compression switch
        {
            CompressionKind.Gzip => level is >= 0 and <= 9,
            CompressionKind.Zstd => level >= Compressor.MinCompressionLevel && level <= Compressor.MaxCompressionLevel,
            CompressionKind.Lz4 => level is 0 or >= 3 and <= 12,
            CompressionKind.Brotli => level is >= 0 and <= 11,
            _ => false
        };
        if (!valid)
            throw new ArgumentOutOfRangeException(nameof(CompressionLevel), level,
                $"Compression level '{level}' is not supported for '{Compression}'.");
    }
}
