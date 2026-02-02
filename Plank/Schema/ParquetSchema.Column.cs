using System.Collections.Immutable;

namespace Plank;

public sealed partial class ParquetSchema
{
    public abstract class Column
    {
        protected Column(int ordinal, string name, ParquetPhysicalType physicalType, ParquetRepetition repetition, ImmutableArray<EncodingKind> encodings, Type clrType)
        {
            if (ordinal < 0)
                throw new ArgumentOutOfRangeException(nameof(ordinal), ordinal, "Column ordinal must be non-negative.");
            if (name is null)
                throw new ArgumentNullException(nameof(name));

            Ordinal = ordinal;
            Name = name;
            PhysicalType = physicalType;
            Repetition = repetition;
            Encodings = encodings.IsDefault ? ImmutableArray<EncodingKind>.Empty : encodings;
            ClrType = clrType ?? throw new ArgumentNullException(nameof(clrType));
        }

        public int Ordinal { get; }

        public string Name { get; }

        public ParquetPhysicalType PhysicalType { get; }

        public ParquetRepetition Repetition { get; }

        public ImmutableArray<EncodingKind> Encodings { get; }

        public Type ClrType { get; }
    }

    public sealed class Column<TProp> : Column
    {
        public Column(int ordinal, string name, ColumnOptions options)
            : base(
                ordinal,
                name,
                ParquetTypeMap.GetPhysicalType(typeof(TProp)),
                ResolveRepetition(options),
                options.Encodings,
                typeof(TProp))
        {
        }

        static ParquetRepetition ResolveRepetition(ColumnOptions options)
        {
            var repetition = options.Repetition;
            if (repetition == ParquetRepetition.Unspecified)
                repetition = ParquetTypeMap.IsNullable(typeof(TProp))
                    ? ParquetRepetition.Optional
                    : ParquetRepetition.Required;

            return repetition;
        }
    }
}
