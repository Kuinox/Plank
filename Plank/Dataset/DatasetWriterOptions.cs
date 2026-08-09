using Plank.Writing;

namespace Plank.Dataset;

/// <summary>Controls the bounded resources of a generated dataset writer.</summary>
public sealed class DatasetWriterOptions
{
    /// <summary>Gets the default dataset writer options.</summary>
    public static readonly DatasetWriterOptions Default = new();

    /// <summary>Gets or initializes the options used when an existing dataset file is opened again.</summary>
    public ParquetAppendOptions AppendOptions { get; init; } = ParquetAppendOptions.Default;

    /// <summary>Gets or initializes the options used when the dataset writer creates new files.</summary>
    public ParquetWriterOptions WriterOptions { get; init; } = ParquetWriterOptions.Default;

    /// <summary>Gets or initializes the maximum number of rows shared by all inactive partitions.</summary>
    /// <remarks>A value of zero makes a new partition take an active writer immediately.</remarks>
    public uint PendingRowCapacity { get; init; } = 4096;

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(AppendOptions);
        AppendOptions.Validate();
        ArgumentNullException.ThrowIfNull(WriterOptions);
        WriterOptions.Validate();
        if (PendingRowCapacity > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(PendingRowCapacity), PendingRowCapacity,
                $"Pending row capacity must be <= {int.MaxValue}.");
    }
}
