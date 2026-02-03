namespace Plank.Schema;

public sealed record Column(string Name, ParquetPhysicalType PhysicalType, ColumnOptions Options)
{
    public void Validate()
        => ArgumentNullException.ThrowIfNull(Name);
}
