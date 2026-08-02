namespace Plank.Writing;

readonly struct RepeatedRowShape
{
    internal RepeatedRowShape(int[] tokens, int[] rowOffsets)
    {
        Tokens = tokens;
        RowOffsets = rowOffsets;
    }

    internal readonly int[]? Tokens;
    internal readonly int[]? RowOffsets;
}
