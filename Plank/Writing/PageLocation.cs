namespace Plank.Writing;

internal readonly struct PageLocation
{
    internal PageLocation(long offset, int compressedPageSize, long firstRowIndex)
    {
        Offset = offset;
        CompressedPageSize = compressedPageSize;
        FirstRowIndex = firstRowIndex;
    }

    internal long Offset { get; }

    internal int CompressedPageSize { get; }

    internal long FirstRowIndex { get; }
}
