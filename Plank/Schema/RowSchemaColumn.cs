using Plank.Writing.PageStrategy;

namespace Plank.Schema;

public sealed record RowSchemaColumn
{
    public RowSchemaColumn(string name, ParquetPhysicalType physicalType, Type clrType, ColumnOptions? options = null,
        LogicalType? logicalType = null, IPageStrategy? pageStrategy = null)
        : this(name, physicalType, clrType, fieldId: null, options, logicalType, pageStrategy)
    {
    }

    public RowSchemaColumn(string name, ParquetPhysicalType physicalType, Type clrType, int fieldId,
        ColumnOptions? options = null, LogicalType? logicalType = null, IPageStrategy? pageStrategy = null)
        : this(name, physicalType, clrType, (int?)fieldId, options, logicalType, pageStrategy)
    {
    }

    RowSchemaColumn(string name, ParquetPhysicalType physicalType, Type clrType, int? fieldId, ColumnOptions? options,
        LogicalType? logicalType, IPageStrategy? pageStrategy)
    {
        Name = name;
        PhysicalType = physicalType;
        ClrType = clrType;
        Options = options ?? ColumnOptions.Default;
        LogicalType = logicalType;
        PageStrategy = pageStrategy;
        FieldId = fieldId;
        EncodingCompatibility.Validate(Name, PhysicalType, Options);
    }

    public string Name { get; }

    public ParquetPhysicalType PhysicalType { get; }

    public Type ClrType { get; }

    public ColumnOptions Options { get; }

    public LogicalType? LogicalType { get; }

    public IPageStrategy? PageStrategy { get; }

    public int? FieldId { get; }

    internal ColumnDefinition ToDefinition()
        => ColumnDefinition.Leaf(Name, PhysicalType, Options, LogicalType, PageStrategy) with { FieldId = FieldId };

}
