using Plank.Schema;

namespace Plank.Reading.Physical.Internal;

readonly struct PhysicalSchemaNode
{
    internal PhysicalSchemaNode(int parentOrdinal, NodeKind kind, ParquetRepetition repetition,
        ParquetPhysicalType? physicalType, uint typeLength, LogicalTypeInfo logicalType, int nameOffset, int nameLength,
        int childCount)
    {
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

    internal int ParentOrdinal { get; }
    internal NodeKind Kind { get; }
    internal ParquetRepetition Repetition { get; }
    internal ParquetPhysicalType? PhysicalType { get; }
    internal uint TypeLength { get; }
    internal LogicalTypeInfo LogicalType { get; }
    internal int NameOffset { get; }
    internal int NameLength { get; }
    internal int ChildCount { get; }
}
