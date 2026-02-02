using System.Collections.Immutable;

namespace Plank;

public readonly struct ColumnOptions
{
    public static readonly ColumnOptions Default = new(ParquetRepetition.Unspecified, ImmutableArray<EncodingKind>.Empty);

    public ColumnOptions(ParquetRepetition repetition, ImmutableArray<EncodingKind> encodings)
    {
        Repetition = repetition;
        Encodings = encodings.IsDefault ? ImmutableArray<EncodingKind>.Empty : encodings;
    }

    public ParquetRepetition Repetition { get; }

    public ImmutableArray<EncodingKind> Encodings { get; }

    public ColumnOptions WithRepetition(ParquetRepetition repetition)
        => new(repetition, Encodings);

    public ColumnOptions WithEncoding(EncodingKind encoding)
        => new(Repetition, Encodings.Add(encoding));
}
