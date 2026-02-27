namespace Plank.DictionaryLab;

public sealed class ReadOnlyMemoryByteComparer : IEqualityComparer<ReadOnlyMemory<byte>>
{
    public static ReadOnlyMemoryByteComparer Instance { get; } = new();

    ReadOnlyMemoryByteComparer()
    {
    }

    public bool Equals(ReadOnlyMemory<byte> x, ReadOnlyMemory<byte> y)
        => x.Span.SequenceEqual(y.Span);

    public int GetHashCode(ReadOnlyMemory<byte> obj)
    {
        var hash = new HashCode();
        hash.AddBytes(obj.Span);
        return hash.ToHashCode();
    }
}
