namespace Plank.Writing;

internal readonly struct PageIndex
{
    internal PageIndex(ColumnStatistics[] statistics, bool[] nullPages, PageLocation[] locations, int count)
    {
        Statistics = statistics ?? [];
        NullPages = nullPages ?? [];
        Locations = locations ?? [];
        Count = count;
    }

    internal ColumnStatistics[] Statistics { get; }

    internal bool[] NullPages { get; }

    internal PageLocation[] Locations { get; }

    internal int Count { get; }

    internal bool HasPages
        => Count != 0;
}
