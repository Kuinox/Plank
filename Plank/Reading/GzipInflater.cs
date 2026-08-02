using Plank.Internal.Compression;

namespace Plank.Reading;

unsafe static class GzipInflater
{
    internal static int Decompress(ReadOnlySpan<byte> input, Span<byte> destination)
    {
        Span<byte> streamState = stackalloc byte[ZlibNative.StreamStateSize];
        streamState.Clear();
        Span<byte> outputBuffer = destination.IsEmpty ? stackalloc byte[1] : destination;
        fixed (byte* stream = streamState)
        fixed (byte* inputStart = input)
        fixed (byte* output = outputBuffer)
        {
            var version = ZlibNative.GetVersion();
            var initCode = ZlibNative.InflateInit2(stream, ZlibNative.WindowBitsGzip, (byte*)version,
                ZlibNative.StreamStateSize);
            if (initCode != ZlibNative.ResultOk)
                throw new InvalidDataException($"zlib inflateInit2_ failed with code {initCode}.");

            try
            {
                ZlibNative.SetInput(stream, inputStart, input.Length);
                ZlibNative.SetOutput(stream, output, outputBuffer.Length);

                var resultCode = ZlibNative.Inflate(stream, ZlibNative.FlushFinish);
                if (resultCode != ZlibNative.ResultStreamEnd)
                    throw new InvalidDataException($"zlib inflate failed with code {resultCode}.");

                var written = outputBuffer.Length - checked((int)ZlibNative.GetAvailableOutput(stream));
                if (ZlibNative.GetAvailableInput(stream) != 0)
                    throw new InvalidDataException("Gzip payload contains trailing compressed bytes.");
                return written;
            }
            finally
            {
                ZlibNative.InflateEnd(stream);
            }
        }
    }
}
