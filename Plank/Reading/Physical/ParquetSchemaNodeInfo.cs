using Plank.Schema;

namespace Plank.Reading.Physical;

public readonly struct ParquetSchemaNodeInfo
{
    internal readonly int NameOffset;
    internal readonly int NameLength;

    internal ParquetSchemaNodeInfo(int ordinal, int parentOrdinal, NodeKind kind, ParquetRepetition repetition,
        ParquetPhysicalType? physicalType, uint typeLength, LogicalTypeInfo logicalType, int nameOffset, int nameLength,
        int childCount)
    {
        Ordinal = ordinal;
        ParentOrdinal = parentOrdinal;
        Kind = kind;
        Repetition = repetition;
        PhysicalType = physicalType;
        TypeLength = typeLength;
        LogicalType = logicalType;
        NameOffset = nameOffset;
        NameLength = nameLength;
        ChildCount = childCount;
    }

    public int Ordinal { get; }
    public int ParentOrdinal { get; }
    public NodeKind Kind { get; }
    public ParquetRepetition Repetition { get; }
    public ParquetPhysicalType? PhysicalType { get; }
    public uint TypeLength { get; }
    public LogicalTypeInfo LogicalType { get; }
    public int ChildCount { get; }
}
