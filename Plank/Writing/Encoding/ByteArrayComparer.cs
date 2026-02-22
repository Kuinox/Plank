namespace Plank.Writing.Encoding;

sealed class ByteArrayComparer : IEqualityComparer<byte[]>
{
    internal static readonly ByteArrayComparer Instance = new();

    public bool Equals(byte[]? x, byte[]? y)
    {
        if (ReferenceEquals(x, y))
            return true;
        if (x is null || y is null)
            return false;

        return x.AsSpan().SequenceEqual(y);
    }

    public int GetHashCode(byte[] obj)
    {
        ArgumentNullException.ThrowIfNull(obj);
        unchecked
        {
            var hash = 17;
            for (var i = 0; i < obj.Length; i++)
                hash = hash * 31 + obj[i];

            return hash;
        }
    }
}
