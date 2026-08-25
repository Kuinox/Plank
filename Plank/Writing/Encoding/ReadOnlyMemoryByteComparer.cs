namespace Plank.Writing.Encoding;

sealed class ReadOnlyMemoryByteComparer : IEqualityComparer<ReadOnlyMemory<byte>>
{
    internal static readonly ReadOnlyMemoryByteComparer Instance = new();

    public bool Equals(ReadOnlyMemory<byte> x, ReadOnlyMemory<byte> y)
        => x.Span.SequenceEqual(y.Span);

    public int GetHashCode(ReadOnlyMemory<byte> obj)
    {
        var hash = new HashCode();
        hash.AddBytes(obj.Span);
        return hash.ToHashCode();
    }
}
