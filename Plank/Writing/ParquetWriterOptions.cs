using Plank;

namespace Plank.Writing;

public sealed class ParquetWriterOptions
{
    public static readonly ParquetWriterOptions Default = new();

    public PageWriteMode PageWriteMode { get; init; } = PageWriteMode.Buffered;

    public IParquetLog Log { get; init; } = ParquetLog.None;
}
