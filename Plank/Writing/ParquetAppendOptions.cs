namespace Plank.Writing;

public sealed class ParquetAppendOptions
{
    public static readonly ParquetAppendOptions Default = new();

    public ParquetWriterOptions WriterOptions { get; init; } = ParquetWriterOptions.Default;

    public bool PreserveExistingMetadata { get; init; } = true;

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(WriterOptions);
        WriterOptions.Validate();
    }
}
