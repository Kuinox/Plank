namespace Plank.Writing;

internal readonly struct PageLocation
{
    internal PageLocation(long offset, uint compressedPageSize, long firstRowIndex)
    {
        Offset = offset;
        CompressedPageSize = compressedPageSize;
        FirstRowIndex = firstRowIndex;
    }

    internal long Offset { get; }

    internal uint CompressedPageSize { get; }

    internal long FirstRowIndex { get; }
}
