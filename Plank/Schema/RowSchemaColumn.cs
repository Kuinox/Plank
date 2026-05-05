namespace Plank.Schema;

public sealed record RowSchemaColumn
{
    public RowSchemaColumn(string name, ParquetPhysicalType physicalType, Type clrType, ColumnOptions? options = null,
        LogicalType? logicalType = null)
    {
        Name = name;
        PhysicalType = physicalType;
        ClrType = clrType;
        Options = options ?? ColumnOptions.Default;
        LogicalType = logicalType;
        EncodingCompatibility.Validate(Name, PhysicalType, Options);
    }

    public string Name { get; }

    public ParquetPhysicalType PhysicalType { get; }

    public Type ClrType { get; }

    public ColumnOptions Options { get; }

    public LogicalType? LogicalType { get; }

    internal Column ToColumn()
        => new(Name, PhysicalType, Options, LogicalType);

}
