namespace Plank.Schema;

public sealed record Column
{
    public Column(string name, ParquetPhysicalType physicalType, ColumnOptions? options = null)
    {
        Name = name;
        PhysicalType = physicalType;
        Options = options ?? ColumnOptions.Default;
    }

    public string Name { get; }

    public ParquetPhysicalType PhysicalType { get; }

    public ColumnOptions Options { get; }

    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Name);
        if (string.IsNullOrWhiteSpace(Name))
            throw new ArgumentException("Column name must not be empty or whitespace.", nameof(Name));
        if (!Enum.IsDefined(PhysicalType))
            throw new ArgumentOutOfRangeException(nameof(PhysicalType), PhysicalType, "PhysicalType must be a defined ParquetPhysicalType value.");

        Options.Validate();
    }
}
