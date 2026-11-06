using Plank.Schema;

namespace Plank.Reading.Physical;

public readonly struct ParquetColumnSchemaInfo
{
    internal readonly int NodeOrdinal;
    // Levels describe the leaf and its ancestors below the schema root.
    internal readonly int MaxRepetitionLevel;
    internal readonly int MaxDefinitionLevel;
    internal bool IsRepeated => MaxRepetitionLevel > 0;
    internal bool HasDefinitionLevels => MaxDefinitionLevel > 0;

    internal ParquetColumnSchemaInfo(int ordinal, int nodeOrdinal, int pathSegmentCount,
        ParquetPhysicalType physicalType, uint typeLength, LogicalTypeInfo logicalType, int maxRepetitionLevel,
        int maxDefinitionLevel)
    {
        Ordinal = ordinal;
        NodeOrdinal = nodeOrdinal;
        MaxRepetitionLevel = maxRepetitionLevel;
        MaxDefinitionLevel = maxDefinitionLevel;
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
