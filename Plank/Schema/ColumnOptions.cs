using System.Collections.Immutable;

namespace Plank.Schema;

public sealed record ColumnOptions
{
    /// <param name="allowMissing">
    /// Allows a requested column to be absent from older Parquet files. This is schema-evolution tolerance,
    /// not Parquet nullability; use <paramref name="repetition"/> to control whether present values can be null.
    /// </param>
    public ColumnOptions(ParquetRepetition repetition = ParquetRepetition.Unspecified,
        ImmutableArray<EncodingKind> encodings = default, uint typeLength = 0, bool allowMissing = false)
    {
        Repetition = repetition;
        Encodings = encodings.IsDefault ? [] : encodings;
        TypeLength = typeLength;
        AllowMissing = allowMissing;
    }

    public static readonly ColumnOptions Default = new();

    public ParquetRepetition Repetition { get; }

    public ImmutableArray<EncodingKind> Encodings { get; }

    public uint TypeLength { get; }

    /// <inheritdoc cref="ColumnOptions(ParquetRepetition, ImmutableArray{EncodingKind}, uint, bool)" path="/param[@name='allowMissing']"/>
    public bool AllowMissing { get; }

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
        if (AllowMissing != other.AllowMissing)
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
        hash.Add(AllowMissing);
        foreach (var encoding in Encodings)
            hash.Add(encoding);

        return hash.ToHashCode();
    }

}
