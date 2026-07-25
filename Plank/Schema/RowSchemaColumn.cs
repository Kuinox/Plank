using Plank.Writing.PageStrategy;

namespace Plank.Schema;

public sealed record RowSchemaColumn
{
    public RowSchemaColumn(string name, ParquetPhysicalType physicalType, Type clrType, ColumnOptions? options = null,
        LogicalType? logicalType = null, IPageStrategy? pageStrategy = null)
    {
        Name = name;
        PhysicalType = physicalType;
        ClrType = clrType;
        Options = options ?? ColumnOptions.Default;
        LogicalType = logicalType;
        PageStrategy = pageStrategy;
        EncodingCompatibility.Validate(Name, PhysicalType, Options);
    }

    public string Name { get; }

    public ParquetPhysicalType PhysicalType { get; }

    public Type ClrType { get; }

    public ColumnOptions Options { get; }

    public LogicalType? LogicalType { get; }

    public IPageStrategy? PageStrategy { get; }

    internal ColumnDefinition ToDefinition()
        => ColumnDefinition.Leaf(Name, PhysicalType, Options, LogicalType, PageStrategy);

}
