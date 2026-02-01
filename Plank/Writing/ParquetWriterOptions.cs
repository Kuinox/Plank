namespace Plank;

public sealed class ParquetWriterOptions
{
    public static readonly ParquetWriterOptions Default = new();

    public PageWriteMode PageWriteMode { get; init; } = PageWriteMode.Buffered;

    public bool AllowBufferedFallback { get; init; } = true;
}
