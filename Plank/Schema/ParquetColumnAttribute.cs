namespace Plank.Schema;

[AttributeUsage(AttributeTargets.Property)]
public sealed class ParquetColumnAttribute : Attribute
{
    public ParquetColumnAttribute() { }

    public ParquetColumnAttribute(string name)
        => Name = name;

    public ParquetColumnAttribute(ParquetPhysicalType physicalType)
    {
        PhysicalType = physicalType;
        HasPhysicalType = true;
    }

    public ParquetColumnAttribute(string name, ParquetPhysicalType physicalType)
    {
        Name = name;
        PhysicalType = physicalType;
        HasPhysicalType = true;
    }

    public string? Name { get; }

    public ParquetPhysicalType PhysicalType { get; }

    public bool HasPhysicalType { get; }

    public EncodingKind[]? Encodings { get; set; }

    /// <summary>Gets or sets the compression used when writing this column.</summary>
    public CompressionKind Compression { get; set; }

    /// <summary>Gets or sets the codec-specific compression level used when writing this column.</summary>
    public int CompressionLevel { get; set; }

    public LogicalTypeKind LogicalType { get; set; }

    public int FieldId { get; set; }

    public int Precision { get; set; }

    public int Scale { get; set; }

    /// <summary>Gets or sets the parameterless custom converter type for this property.</summary>
    public Type? Converter { get; set; }

    /// <summary>Gets or sets whether generated row writers emit a Bloom filter for this column.</summary>
    public bool BloomFilter { get; set; }

    /// <summary>Gets or sets the Bloom filter's target false-positive probability.</summary>
    public double BloomFilterFalsePositiveProbability { get; set; } = 0.01;

    /// <summary>Gets or sets the expected distinct values per row group, or zero to use the value count.</summary>
    public uint BloomFilterExpectedDistinctValueCount { get; set; }

    /// <summary>Gets or sets the maximum Bloom-filter bitset size in bytes.</summary>
    public uint BloomFilterMaximumBytes { get; set; } = 128 * 1024 * 1024;
}
