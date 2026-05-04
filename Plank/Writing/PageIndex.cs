namespace Plank.Writing;

internal readonly struct PageIndex
{
    internal PageIndex(ColumnStatistics[] statistics, PageLocation[] locations, int count)
    {
        Statistics = statistics ?? [];
        Locations = locations ?? [];
        Count = count;
    }

    internal ColumnStatistics[] Statistics { get; }

    internal PageLocation[] Locations { get; }

    internal int Count { get; }

    internal bool HasPages
        => Count != 0;
}
