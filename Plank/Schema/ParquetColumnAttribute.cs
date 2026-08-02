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

    public LogicalTypeKind LogicalType { get; set; }

    /// <summary>Gets or sets the precision when <see cref="LogicalType"/> is <see cref="LogicalTypeKind.Decimal"/>.</summary>
    public int DecimalPrecision { get; set; }

    /// <summary>Gets or sets the scale when <see cref="LogicalType"/> is <see cref="LogicalTypeKind.Decimal"/>.</summary>
    public int DecimalScale { get; set; }

    /// <summary>Gets or sets the parameterless custom converter type for this property.</summary>
    public Type? Converter { get; set; }
}
