using Plank.Schema;
using Plank.Reading.Physical.Internal;

namespace Plank.Reading.Physical;

public readonly struct ParquetColumnSchemaInfo
{
    readonly ParquetFileReader _owner;
    readonly int _version;

    internal ParquetColumnSchemaInfo(ParquetFileReader owner, int version, int ordinal)
    {
        _owner = owner;
        _version = version;
        Ordinal = ordinal;
    }

    public int Ordinal { get; }
    public ParquetPhysicalType PhysicalType => Node.PhysicalType!.Value;
    public uint TypeLength => Node.TypeLength;
    public LogicalTypeInfo LogicalType => Node.LogicalType;
    public int PathSegmentCount => Column.PathSegmentCount;

    public ReadOnlySpan<byte> PathSegmentUtf8(int segmentOrdinal)
        => _owner.GetName(_version, _owner.GetPathNodeOrdinal(_version, Ordinal, segmentOrdinal));

    PhysicalColumnSchema Column
        => _owner.GetColumnSchema(_version, Ordinal);

    PhysicalSchemaNode Node
        => _owner.GetSchemaNode(_version, Column.NodeOrdinal);
}
