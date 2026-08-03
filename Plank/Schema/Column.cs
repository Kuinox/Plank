using Plank.Writing.PageStrategy;

namespace Plank.Schema;

internal sealed record Column
{
    internal Column(string name, ParquetPhysicalType physicalType, ColumnOptions? options = null,
        LogicalType? logicalType = null, IPageStrategy? pageStrategy = null, ParquetValueConverter? converter = null)
    {
        Name = name;
        PhysicalType = physicalType;
        Options = options ?? ColumnOptions.Default;
        LogicalType = logicalType;
        PageStrategy = pageStrategy;
        Converter = converter;
        EncodingCompatibility.Validate(this);
        ColumnDefinition.ValidateLogicalType(name, physicalType, Options, logicalType);
        ColumnDefinition.ValidateConverter(name, physicalType, Options, converter);
        if (Options.BloomFilter is not null && physicalType == ParquetPhysicalType.Boolean)
            throw new NotSupportedException(
                $"Column '{name}' cannot use a Bloom filter because Parquet does not define standalone boolean hashing.");
    }

    public string Name { get; }

    public ParquetPhysicalType PhysicalType { get; }

    public ColumnOptions Options { get; }

    public LogicalType? LogicalType { get; }

    public ParquetValueConverter? Converter { get; }

    internal IPageStrategy? PageStrategy { get; }

}
