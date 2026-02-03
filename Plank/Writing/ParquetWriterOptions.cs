using Plank;

namespace Plank.Writing;

public sealed class ParquetWriterOptions
{
    public static readonly ParquetWriterOptions Default = new();

    public IParquetLog Log { get; init; } = ParquetLog.None;

    public uint? ExpectedRowGroupCount { get; init; }

    public uint? RowGroupRowCountHint { get; init; }
}
