using System.Collections.Immutable;

namespace Plank.Schema;

public readonly record struct ColumnOptions(ParquetRepetition Repetition, ImmutableArray<EncodingKind> Encodings)
{
    public static readonly ColumnOptions Default = new(ParquetRepetition.Unspecified, ImmutableArray<EncodingKind>.Empty);

    public ColumnOptions WithRepetition(ParquetRepetition repetition)
        => new(repetition, NormalizedEncodings());

    public ColumnOptions Optional()
        => WithRepetition(ParquetRepetition.Optional);

    public ColumnOptions Required()
        => WithRepetition(ParquetRepetition.Required);

    public ColumnOptions WithEncoding(EncodingKind encoding)
        => new(Repetition, NormalizedEncodings().Add(encoding));

    ImmutableArray<EncodingKind> NormalizedEncodings()
        => Encodings.IsDefault ? ImmutableArray<EncodingKind>.Empty : Encodings;
}
