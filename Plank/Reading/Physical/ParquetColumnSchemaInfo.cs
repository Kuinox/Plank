using Plank.Schema;

namespace Plank.Reading.Physical;

public readonly struct ParquetColumnSchemaInfo
{
    internal readonly int NodeOrdinal;
    // These flags describe the leaf and its ancestors below the schema root.
    internal readonly bool IsRepeated;
    internal readonly bool HasDefinitionLevels;

    internal ParquetColumnSchemaInfo(int ordinal, int nodeOrdinal, int pathSegmentCount,
        ParquetPhysicalType physicalType, uint typeLength, LogicalTypeInfo logicalType, bool isRepeated,
        bool hasDefinitionLevels)
    {
        Ordinal = ordinal;
        NodeOrdinal = nodeOrdinal;
        IsRepeated = isRepeated;
        HasDefinitionLevels = hasDefinitionLevels;
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
