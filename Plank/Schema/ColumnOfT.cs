namespace Plank;

public sealed class Column<TProp> : IColumn<TProp>
{
    internal Column(int ordinal, string name, ColumnOptions options)
    {
        if (ordinal < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ordinal), ordinal, "Column ordinal must be non-negative.");
        }

        if (name is null)
        {
            throw new ArgumentNullException(nameof(name));
        }

        Ordinal = ordinal;
        Name = name;
        PhysicalType = ParquetTypeMap.GetPhysicalType(typeof(TProp));

        var repetition = options.Repetition;
        if (repetition == ParquetRepetition.Unspecified)
        {
            repetition = ParquetTypeMap.IsNullable(typeof(TProp))
                ? ParquetRepetition.Optional
                : ParquetRepetition.Required;
        }

        Repetition = repetition;
        Encodings = options.Encodings.Length == 0
            ? Array.Empty<EncodingKind>()
            : (EncodingKind[])options.Encodings.Clone();
    }

    public string Name { get; }

    public int Ordinal { get; }

    public ParquetPhysicalType PhysicalType { get; }

    public ParquetRepetition Repetition { get; }

    public EncodingKind[] Encodings { get; }

    public Type ClrType => typeof(TProp);
}
