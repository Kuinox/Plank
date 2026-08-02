using Plank.Writing.PageStrategy;

namespace Plank.Schema;

internal sealed record Column
{
    internal Column(string name, ParquetPhysicalType physicalType, ColumnOptions? options = null,
        LogicalType? logicalType = null, IPageStrategy? pageStrategy = null, int? fieldId = null)
    {
        Name = name;
        PhysicalType = physicalType;
        Options = options ?? ColumnOptions.Default;
        LogicalType = logicalType;
        FieldId = fieldId;
        PageStrategy = pageStrategy;
        EncodingCompatibility.Validate(this);
        ColumnDefinition.ValidateLogicalType(name, physicalType, logicalType);
    }

    public string Name { get; }

    public ParquetPhysicalType PhysicalType { get; }

    public ColumnOptions Options { get; }

    public LogicalType? LogicalType { get; }

    public int? FieldId { get; }

    internal IPageStrategy? PageStrategy { get; }

}
