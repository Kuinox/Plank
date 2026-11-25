namespace Plank.Writing;

public sealed class ParquetAppendOptions
{
    public static readonly ParquetAppendOptions Default = new();

    public ParquetWriterOptions WriterOptions { get; init; } = ParquetWriterOptions.Default;

    public bool PreserveExistingMetadata { get; init; } = true;

    public bool AppendToLatestRowGroup { get; init; }

    /// <summary>
    /// Keeps the existing footer in an in-place destination until the first import, so the file stays readable
    /// while nothing has been merged into it and after an import that rolled back.
    /// </summary>
    internal bool DeferFooterTruncation { get; init; }

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(WriterOptions);
        WriterOptions.Validate();
    }
}
