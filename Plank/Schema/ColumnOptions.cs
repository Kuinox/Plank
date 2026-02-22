using System.Collections.Immutable;

namespace Plank.Schema;

public sealed record ColumnOptions
{
    public ColumnOptions(ParquetRepetition repetition = ParquetRepetition.Unspecified,
        ImmutableArray<EncodingKind> encodings = default, uint typeLength = 0)
    {
        Repetition = repetition;
        Encodings = encodings.IsDefault ? [] : encodings;
        TypeLength = typeLength;
    }

    public static readonly ColumnOptions Default = new();

    public ParquetRepetition Repetition { get; }

    public ImmutableArray<EncodingKind> Encodings { get; }

    public uint TypeLength { get; }

    public bool Equals(ColumnOptions? other)
    {
        if (ReferenceEquals(this, other))
            return true;
        if (other is null)
            return false;
        if (Repetition != other.Repetition)
            return false;
        if (TypeLength != other.TypeLength)
            return false;
        if (Encodings.Length != other.Encodings.Length)
            return false;

        for (var i = 0; i < Encodings.Length; i++)
            if (Encodings[i] != other.Encodings[i])
                return false;

        return true;
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Repetition);
        hash.Add(TypeLength);
        foreach (var encoding in Encodings)
            hash.Add(encoding);

        return hash.ToHashCode();
    }

    public void Validate()
    {
        if (!Enum.IsDefined(Repetition))
            throw new ArgumentOutOfRangeException(nameof(Repetition), Repetition, "Repetition must be a defined ParquetRepetition value.");

        if (Encodings.IsDefault)
            throw new InvalidOperationException("Encodings must not be a default ImmutableArray.");

        if (Encodings.Length == 0)
            return;

        var seen = new HashSet<EncodingKind>();
        foreach (var encoding in Encodings)
        {
            if (!Enum.IsDefined(encoding))
                throw new ArgumentOutOfRangeException(nameof(Encodings), encoding, "Encoding must be a defined EncodingKind value.");
            if (!seen.Add(encoding))
                throw new InvalidOperationException($"Duplicate encoding '{encoding}' is not allowed.");
        }
    }
}
