using System.Collections.Immutable;

namespace Plank;

public sealed partial class ParquetSchema
{
    public sealed class Column
    {
        public Column(int ordinal, string name, Type clrType, ColumnOptions options)
        {
            if (ordinal < 0)
                throw new ArgumentOutOfRangeException(nameof(ordinal), ordinal, "Column ordinal must be non-negative.");
            if (name is null)
                throw new ArgumentNullException(nameof(name));
            if (clrType is null)
                throw new ArgumentNullException(nameof(clrType));

            Ordinal = ordinal;
            Name = name;
            ClrType = clrType;
            PhysicalType = ParquetTypeMap.GetPhysicalType(clrType);

            var repetition = options.Repetition;
            if (repetition == ParquetRepetition.Unspecified)
                repetition = ParquetTypeMap.IsNullable(clrType)
                    ? ParquetRepetition.Optional
                    : ParquetRepetition.Required;

            Repetition = repetition;
            Encodings = options.Encodings.IsDefault ? ImmutableArray<EncodingKind>.Empty : options.Encodings;
        }

        public int Ordinal { get; }

        public string Name { get; }

        public Type ClrType { get; }

        public ParquetPhysicalType PhysicalType { get; }

        public ParquetRepetition Repetition { get; }

        public ImmutableArray<EncodingKind> Encodings { get; }
    }
}
