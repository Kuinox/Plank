namespace Plank;

public readonly struct ColumnOptions
{
    public static readonly ColumnOptions Default = new(ParquetRepetition.Unspecified, Array.Empty<EncodingKind>());

    public ColumnOptions(ParquetRepetition repetition, EncodingKind[] encodings)
    {
        Repetition = repetition;
        Encodings = encodings ?? Array.Empty<EncodingKind>();
    }

    public ParquetRepetition Repetition { get; }

    public EncodingKind[] Encodings { get; }

    public ColumnOptions WithRepetition(ParquetRepetition repetition)
    {
        return new ColumnOptions(repetition, Encodings);
    }

    public ColumnOptions WithEncoding(EncodingKind encoding)
    {
        var updated = new EncodingKind[Encodings.Length + 1];
        if (Encodings.Length > 0)
        {
            Array.Copy(Encodings, updated, Encodings.Length);
        }

        updated[^1] = encoding;
        return new ColumnOptions(Repetition, updated);
    }
}
