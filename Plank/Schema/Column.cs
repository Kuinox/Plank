namespace Plank.Schema;

public sealed record Column
{
    public Column(string name, ParquetPhysicalType physicalType, ColumnOptions? options = null,
        LogicalType? logicalType = null)
    {
        Name = name;
        PhysicalType = physicalType;
        Options = options ?? ColumnOptions.Default;
        LogicalType = logicalType;
        EncodingCompatibility.Validate(this);
    }

    public string Name { get; }

    public ParquetPhysicalType PhysicalType { get; }

    public ColumnOptions Options { get; }

    public LogicalType? LogicalType { get; }

}
