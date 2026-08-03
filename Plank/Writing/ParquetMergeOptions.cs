namespace Plank.Writing;

public sealed class ParquetMergeOptions
{
    public static readonly ParquetMergeOptions Default = new();

    public ParquetWriterOptions WriterOptions { get; init; } = ParquetWriterOptions.Default;

    public bool PreserveFirstFileMetadata { get; init; } = true;

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(WriterOptions);
        WriterOptions.Validate();
    }
}
