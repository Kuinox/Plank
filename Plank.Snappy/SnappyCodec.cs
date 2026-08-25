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

    public static int GetUncompressedLength(ReadOnlySpan<byte> source)
    {
        unsafe
        {
            nuint result = 0;
            fixed (byte* sourcePointer = source)
            {
                var status = SnappyNative.GetUncompressedLength(sourcePointer, (nuint)source.Length, ref result);
                if (status == SnappyStatus.Ok)
                {
                    if (result > int.MaxValue)
                        throw new InvalidOperationException("Snappy uncompressed length exceeds Int32.MaxValue.");

                    return (int)result;
                }

                if (status == SnappyStatus.InvalidInput)
                    throw new InvalidDataException("Snappy payload is invalid.");
            }
        }

        throw new InvalidOperationException("Snappy uncompressed length query failed with an unknown status code.");
    }

    public static int Decompress(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        var expectedLength = GetUncompressedLength(source);
        if (destination.Length < expectedLength)
            throw new ArgumentException("Destination buffer is too small for the uncompressed payload.", nameof(destination));

        unsafe
        {
            var uncompressedLength = (nuint)destination.Length;
            fixed (byte* sourcePointer = source)
            fixed (byte* destinationPointer = destination)
            {
                var status = SnappyNative.Uncompress(sourcePointer, (nuint)source.Length, destinationPointer,
                    ref uncompressedLength);
                if (status == SnappyStatus.Ok)
                {
                    if (uncompressedLength > int.MaxValue)
                        throw new InvalidOperationException("Snappy uncompressed length exceeds Int32.MaxValue.");

                    return (int)uncompressedLength;
                }

                if (status == SnappyStatus.InvalidInput)
                    throw new InvalidDataException("Snappy payload is invalid.");
            }
        }

        throw new InvalidOperationException("Snappy decompression failed with an unknown status code.");
    }
}
