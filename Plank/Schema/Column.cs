namespace Plank;

public interface IColumn<out TProp> : IColumn
{
}

public sealed class Column<T, TProp> : IColumn<TProp>
{
    public delegate TProp Getter(in T row);
    public delegate void Setter(ref T row, TProp value);

    private readonly Getter _getter;
    private readonly Setter? _setter;

    private Column(int ordinal, string name, ParquetPhysicalType physicalType, ParquetRepetition repetition, EncodingKind[] encodings, Getter getter, Setter? setter)
    {
        Ordinal = ordinal;
        Name = name;
        PhysicalType = physicalType;
        Repetition = repetition;
        Encodings = encodings;
        _getter = getter;
        _setter = setter;
    }

    public string Name { get; }

    public int Ordinal { get; }

    public ParquetPhysicalType PhysicalType { get; }

    public ParquetRepetition Repetition { get; }

    public EncodingKind[] Encodings { get; }

    public Type ClrType => typeof(TProp);

    internal Getter Get => _getter;

    internal Setter? Set => _setter;

    public static Column<T, TProp> Create(
        string name,
        int ordinal,
        Getter getter,
        Setter? setter = null,
        ColumnOptions? options = null)
    {
        if (name is null)
        {
            throw new ArgumentNullException(nameof(name));
        }

        if (getter is null)
        {
            throw new ArgumentNullException(nameof(getter));
        }

        if (ordinal < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ordinal), ordinal, "Column ordinal must be non-negative.");
        }

        var resolvedOptions = options ?? ColumnOptions.Default;
        var repetition = resolvedOptions.Repetition;
        if (repetition == ParquetRepetition.Unspecified)
        {
            repetition = ParquetTypeMap.IsNullable(typeof(TProp))
                ? ParquetRepetition.Optional
                : ParquetRepetition.Required;
        }

        var physicalType = ParquetTypeMap.GetPhysicalType(typeof(TProp));
        var encodings = resolvedOptions.Encodings.Length == 0
            ? Array.Empty<EncodingKind>()
            : (EncodingKind[])resolvedOptions.Encodings.Clone();

        return new Column<T, TProp>(ordinal, name, physicalType, repetition, encodings, getter, setter);
    }
}
