using System.Collections.Immutable;

namespace Plank.Schema;

public sealed record ColumnOptions(
    ParquetRepetition Repetition = ParquetRepetition.Unspecified,
    ImmutableArray<EncodingKind> Encodings = default)
{
    public static readonly ColumnOptions Default = new();

    public bool Equals(ColumnOptions? other)
    {
        if (ReferenceEquals(this, other))
            return true;
        if (other is null)
            return false;
        if (Repetition != other.Repetition)
            return false;
        if (Encodings.Length != other.Encodings.Length)
            return false;

        return !Encodings.Where((t, i) => t != other.Encodings[i]).Any();
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Repetition);
        foreach (var t in Encodings)
            hash.Add(t);

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
