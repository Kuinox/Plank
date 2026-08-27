namespace Plank.Snappy;

public static class SnappyCodec
{
    public static int GetMaxCompressedLength(int sourceLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sourceLength);
        return Snappier.Snappy.GetMaxCompressedLength(sourceLength);
    }

    public static int Compress(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (!TryCompress(source, destination, out var written))
            throw new ArgumentException("Destination buffer is too small for the compressed payload.", nameof(destination));

        return written;
    }

    public static bool TryCompress(ReadOnlySpan<byte> source, Span<byte> destination, out int written)
    {
        if (destination.IsEmpty)
        {
            written = 0;
            return false;
        }

        return Snappier.Snappy.TryCompress(source, destination, out written);
    }

    public static int GetUncompressedLength(ReadOnlySpan<byte> source)
        => Snappier.Snappy.GetUncompressedLength(source);

    public static int Decompress(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        var expectedLength = GetUncompressedLength(source);
        if (destination.Length < expectedLength)
            throw new ArgumentException("Destination buffer is too small for the uncompressed payload.", nameof(destination));

        return Snappier.Snappy.Decompress(source, destination);
    }
}
