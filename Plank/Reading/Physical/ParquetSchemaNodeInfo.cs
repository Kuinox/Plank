using Plank.Schema;
using Plank.Reading.Physical.Internal;

namespace Plank.Reading.Physical;

public readonly struct ParquetSchemaNodeInfo
{
    readonly ParquetFileReader _owner;
    readonly int _version;

    internal ParquetSchemaNodeInfo(ParquetFileReader owner, int version, int ordinal)
    {
        _owner = owner;
        _version = version;
        Ordinal = ordinal;
    }

    public int Ordinal { get; }
    public int ParentOrdinal => Node.ParentOrdinal;
    public NodeKind Kind => Node.Kind;
    public ParquetRepetition Repetition => Node.Repetition;
    public ParquetPhysicalType? PhysicalType => Node.PhysicalType;
    public uint TypeLength => Node.TypeLength;
    public LogicalTypeInfo LogicalType => Node.LogicalType;
    public ReadOnlySpan<byte> NameUtf8 => _owner.GetName(_version, Ordinal);

    PhysicalSchemaNode Node
        => _owner.GetSchemaNode(_version, Ordinal);
}
