using System.Collections.Immutable;

namespace Plank.Schema.Builders;

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
    {
        if (ordinal < 0)
            throw new ArgumentOutOfRangeException(nameof(ordinal), ordinal, "Column ordinal must be non-negative.");
        ArgumentNullException.ThrowIfNull(Name);
        ArgumentNullException.ThrowIfNull(ClrType);

        var physicalType = ParquetTypeMap.GetPhysicalType(ClrType);
        var repetition = Options.Repetition;
        if (repetition == ParquetRepetition.Unspecified)
            repetition = ParquetTypeMap.IsNullable(ClrType)
                ? ParquetRepetition.Optional
                : ParquetRepetition.Required;

        var encodings = Options.Encodings.IsDefault ? ImmutableArray<EncodingKind>.Empty : Options.Encodings;
        return new ParquetSchema.Column(ordinal, Name, ClrType, physicalType, repetition, encodings);
    }
}
