using System.Collections.Immutable;

namespace Plank.Schema;

public readonly record struct ColumnOptions(ParquetRepetition Repetition, ImmutableArray<EncodingKind> Encodings)
{
    public static readonly ColumnOptions Default = new(ParquetRepetition.Unspecified, []);
}
