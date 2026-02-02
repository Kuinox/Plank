namespace Plank;

internal sealed record class ColumnDefinition
{
    public ColumnDefinition(string name, Type clrType)
    {
        Name = name;
        ClrType = clrType;
        Options = ColumnOptions.Default;
    }

    public string Name { get; set; }

    public Type ClrType { get; }

    public ColumnOptions Options { get; set; }

    public ParquetSchema.Column Create(int ordinal)
        => new ParquetSchema.Column(ordinal, Name, ClrType, Options);
}
