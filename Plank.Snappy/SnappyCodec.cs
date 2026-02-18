namespace Plank.Snappy;

public static class SnappyCodec
{
    static SnappyCodec()
        => SnappyLibraryResolver.Register();

    public static int GetMaxCompressedLength(int sourceLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sourceLength);

        var maxLength = SnappyNative.MaxCompressedLength((nuint)sourceLength);
        if (maxLength > int.MaxValue)
            throw new InvalidOperationException("Snappy maximum compressed length exceeds Int32.MaxValue.");

        return (int)maxLength;
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

        var compressedLength = (nuint)destination.Length;
        unsafe
        {
            fixed (byte* sourcePointer = source)
            fixed (byte* destinationPointer = destination)
            {
                var status = SnappyNative.Compress(sourcePointer, (nuint)source.Length, destinationPointer, ref compressedLength);
                if (status == SnappyStatus.Ok)
                {
                    if (compressedLength > int.MaxValue)
                        throw new InvalidOperationException("Snappy compressed length exceeds Int32.MaxValue.");

                    written = (int)compressedLength;
                    return true;
                }

                if (status == SnappyStatus.BufferTooSmall)
                {
                    written = 0;
                    return false;
                }

                if (status == SnappyStatus.InvalidInput)
                    throw new InvalidOperationException("Snappy compression rejected the input payload.");
            }
        }

        throw new InvalidOperationException("Snappy compression failed with an unknown status code.");
    }
}
