namespace Plank.Writing;

public sealed class RowGroupOptions
{
    public static readonly RowGroupOptions Default = new();

    public int MaxCompressedBytes { get; init; } = 4 * 1024 * 1024;

    public int MaxPageValueCount { get; init; } = int.MaxValue;

    public int MaxPageBytes { get; init; } = int.MaxValue;
}
