namespace Plank.Writing;

public sealed class RowGroupOptions
{
    public static readonly RowGroupOptions Default = new();

    public int MaxCompressedBytes { get; init; }

    public int MaxPageValueCount { get; init; } = int.MaxValue;

    public int MaxPageBytes { get; init; } = int.MaxValue;
}
