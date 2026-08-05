using Plank.Writing;

namespace Plank.Dataset;

/// <summary>Controls the bounded resources of a generated dataset writer.</summary>
public sealed class DatasetWriterOptions
{
    /// <summary>Gets the default dataset writer options.</summary>
    public static readonly DatasetWriterOptions Default = new();

    /// <summary>Gets or initializes the options used when an existing dataset file is opened again.</summary>
    public ParquetAppendOptions AppendOptions { get; init; } = ParquetAppendOptions.Default;

    /// <summary>Gets or initializes the maximum number of open Parquet writers.</summary>
    public uint MaximumActiveWriters { get; init; } = 32;

    /// <summary>Gets or initializes the maximum number of inactive partitions that can hold pending rows.</summary>
    public uint MaximumPendingPartitions { get; init; } = 256;

    /// <summary>Gets or initializes the pending row count that activates a writer.</summary>
    public uint RowsBeforeWriterActivation { get; init; } = 1024;

    internal void Validate(int rowBufferCapacity)
    {
        ArgumentNullException.ThrowIfNull(AppendOptions);
        AppendOptions.Validate();
        if (MaximumActiveWriters == 0)
            throw new ArgumentOutOfRangeException(nameof(MaximumActiveWriters), MaximumActiveWriters,
                "Maximum active writers must be greater than zero.");
        if (MaximumActiveWriters > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(MaximumActiveWriters), MaximumActiveWriters,
                $"Maximum active writers must be <= {int.MaxValue}.");
        if (MaximumPendingPartitions > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(MaximumPendingPartitions), MaximumPendingPartitions,
                $"Maximum pending partitions must be <= {int.MaxValue}.");
        if ((ulong)MaximumActiveWriters + MaximumPendingPartitions > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(MaximumPendingPartitions), MaximumPendingPartitions,
                $"The total number of tracked partitions must be <= {int.MaxValue}.");
        if (RowsBeforeWriterActivation == 0 || RowsBeforeWriterActivation > rowBufferCapacity)
            throw new ArgumentOutOfRangeException(nameof(RowsBeforeWriterActivation), RowsBeforeWriterActivation,
                $"Rows before writer activation must be between 1 and {rowBufferCapacity}.");
    }
}
