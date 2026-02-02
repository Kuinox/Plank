namespace Plank;

internal sealed class ColumnDefinition<TProp> : IColumnDefinition
{
    public ColumnDefinition(string name)
    {
        Name = name;
        Options = ColumnOptions.Default;
    }

    public string Name { get; set; }

    public ColumnOptions Options { get; set; }

    public ParquetSchema.Column Create(int ordinal)
        => new ParquetSchema.Column(ordinal, Name, typeof(TProp), Options);
}
