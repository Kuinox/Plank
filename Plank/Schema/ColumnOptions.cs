using System.Collections.Immutable;

namespace Plank.Schema;

public sealed record ColumnOptions
{
    public ColumnOptions(ParquetRepetition repetition = ParquetRepetition.Unspecified,
        ImmutableArray<EncodingKind> encodings = default, uint typeLength = 0,
        CompressionKind? compression = null, int? compressionLevel = null)
    {
        Repetition = repetition;
        Encodings = encodings.IsDefault ? [] : encodings;
        TypeLength = typeLength;
        Compression = compression;
        CompressionLevel = compressionLevel;
    }

    public static readonly ColumnOptions Default = new();

    public ParquetRepetition Repetition { get; }

    public ImmutableArray<EncodingKind> Encodings { get; }

    public uint TypeLength { get; }

    public CompressionKind? Compression { get; }

    public int? CompressionLevel { get; }

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
        if (Compression != other.Compression)
            return false;
        if (CompressionLevel != other.CompressionLevel)
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
        hash.Add(Compression);
        hash.Add(CompressionLevel);
        foreach (var encoding in Encodings)
            hash.Add(encoding);

        return hash.ToHashCode();
    }
}
