using System.Collections.Immutable;

namespace Plank.Schema;

public sealed partial class ParquetSchema
{
    public sealed record class Column
    {
        internal Column(
            int ordinal,
            string name,
            Type clrType,
            ParquetPhysicalType physicalType,
            ParquetRepetition repetition,
            ImmutableArray<EncodingKind> encodings)
        {
            Ordinal = ordinal;
            Name = name;
            ClrType = clrType;
            PhysicalType = physicalType;
            Repetition = repetition;
            Encodings = encodings;
        }

        public int Ordinal { get; }

        public string Name { get; }

        public Type ClrType { get; }

        public ParquetPhysicalType PhysicalType { get; }

        public ParquetRepetition Repetition { get; }

        public ImmutableArray<EncodingKind> Encodings { get; }
    }
}
