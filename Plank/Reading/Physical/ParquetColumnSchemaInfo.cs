using Plank.Schema;

namespace Plank.Reading.Physical;

public readonly struct ParquetColumnSchemaInfo
{
    internal readonly int NodeOrdinal;

    internal ParquetColumnSchemaInfo(int ordinal, int nodeOrdinal, int pathSegmentCount,
        ParquetPhysicalType physicalType, uint typeLength, LogicalTypeInfo logicalType)
    {
        Ordinal = ordinal;
        NodeOrdinal = nodeOrdinal;
        PathSegmentCount = pathSegmentCount;
        PhysicalType = physicalType;
        TypeLength = typeLength;
        LogicalType = logicalType;
    }

    public int Ordinal { get; }
    public int PathSegmentCount { get; }
    public ParquetPhysicalType PhysicalType { get; }
    public uint TypeLength { get; }
    public LogicalTypeInfo LogicalType { get; }
}
