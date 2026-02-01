namespace Plank;

public sealed class RowGroupOptions
{
    public static readonly RowGroupOptions Default = new();

    public int MaxEncodedBytes { get; init; } = 4 * 1024 * 1024;

    public int MaxCompressedBytes { get; init; } = 4 * 1024 * 1024;
}
