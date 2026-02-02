using System.Collections.Immutable;

namespace Plank;

public readonly record struct ColumnOptions(ParquetRepetition repetition, ImmutableArray<EncodingKind> encodings)
{
    public static readonly ColumnOptions Default = new(ParquetRepetition.Unspecified, ImmutableArray<EncodingKind>.Empty);

    public ParquetRepetition Repetition { get; } = repetition;

    public ImmutableArray<EncodingKind> Encodings { get; } = encodings.IsDefault ? ImmutableArray<EncodingKind>.Empty : encodings;

    public ColumnOptions WithRepetition(ParquetRepetition repetition)
        => new(repetition, Encodings);

    public ColumnOptions WithEncoding(EncodingKind encoding)
        => new(Repetition, Encodings.Add(encoding));
}
