namespace Plank.Schema;

public sealed record RowSchemaColumn
{
    public RowSchemaColumn(string name, ParquetPhysicalType physicalType, Type clrType, ColumnOptions? options = null)
    {
        Name = name;
        PhysicalType = physicalType;
        ClrType = clrType;
        Options = options ?? ColumnOptions.Default;
    }

    public string Name { get; }

    public ParquetPhysicalType PhysicalType { get; }

    public Type ClrType { get; }

    public ColumnOptions Options { get; }

    internal Column ToColumn()
        => new(Name, PhysicalType, Options);

    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Name);
        if (string.IsNullOrWhiteSpace(Name))
            throw new ArgumentException("Column name must not be empty or whitespace.", nameof(Name));
        if (!Enum.IsDefined(PhysicalType))
            throw new ArgumentOutOfRangeException(nameof(PhysicalType), PhysicalType, "PhysicalType must be a defined ParquetPhysicalType value.");
        ArgumentNullException.ThrowIfNull(ClrType);

        var resolution = ParquetTypeMap.ResolvePhysicalType(ClrType);
        if (!resolution.IsSuccess)
            throw new NotSupportedException(resolution.ErrorMessage);
        if (resolution.PhysicalType != PhysicalType)
            throw new InvalidOperationException(
                $"Column '{Name}' maps CLR type '{ClrType}' to physical type '{resolution.PhysicalType}', but column physical type is '{PhysicalType}'.");

        Options.Validate();
    }
}
