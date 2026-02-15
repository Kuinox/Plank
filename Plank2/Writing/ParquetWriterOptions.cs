namespace Plank2.Writing;

public sealed class ParquetWriterOptions
{
    public static readonly ParquetWriterOptions Default = new();

    public IParquetLog Log { get; init; } = ParquetLog.None;

    public int InitialPageCapacity { get; init; } = 4;
}
