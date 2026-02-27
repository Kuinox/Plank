namespace Plank.DictionaryLab.Nodes;

static class ByteHashing
{
    public static int Hash(ReadOnlySpan<byte> bytes)
    {
        unchecked
        {
            const uint offset = 2166136261;
            const uint prime = 16777619;
            var hash = offset;
            for (var i = 0; i < bytes.Length; i++)
                hash = (hash ^ bytes[i]) * prime;
            return (int)hash;
        }
    }
}
