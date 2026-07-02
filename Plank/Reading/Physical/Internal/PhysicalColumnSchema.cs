namespace Plank.Reading.Physical.Internal;

readonly struct PhysicalColumnSchema
{
    internal PhysicalColumnSchema(int nodeOrdinal, int pathSegmentCount)
    {
        NodeOrdinal = nodeOrdinal;
        PathSegmentCount = pathSegmentCount;
    }

    internal int NodeOrdinal { get; }
    internal int PathSegmentCount { get; }
}
