using Plank.Internal.Compression;

namespace Plank.Reading;

unsafe static class GzipInflater
{
    internal static int Decompress(ReadOnlySpan<byte> input, Span<byte> destination)
    {
        Span<byte> streamState = stackalloc byte[ZlibNative.StreamStateSize];
        Span<byte> overflowProbe = stackalloc byte[1];
        var consumed = 0;
        var written = 0;
        do
        {
            streamState.Clear();
            var remainingOutput = destination[written..];
            // Empty members are valid even after the declared output has been filled.
            // A one-byte probe lets zlib finish them while detecting excess output.
            Span<byte> outputBuffer = remainingOutput.IsEmpty ? overflowProbe : remainingOutput;
            fixed (byte* stream = streamState)
            fixed (byte* inputStart = input)
            fixed (byte* output = outputBuffer)
            {
                var version = ZlibNative.GetVersion();
                var initCode = ZlibNative.InflateInit2(stream, ZlibNative.WindowBitsGzip, (byte*)version,
                    ZlibNative.StreamStateSize);
                if (initCode != ZlibNative.ResultOk)
                    throw new CorruptParquetException($"zlib inflateInit2_ failed with code {initCode}.");

                try
                {
                    ZlibNative.SetInput(stream, inputStart + consumed, input.Length - consumed);
                    ZlibNative.SetOutput(stream, output, outputBuffer.Length);

                    var resultCode = ZlibNative.Inflate(stream, ZlibNative.FlushFinish);
                    if (resultCode != ZlibNative.ResultStreamEnd)
                        throw new CorruptParquetException($"zlib inflate failed with code {resultCode}.");

                    var memberWritten = outputBuffer.Length - checked((int)ZlibNative.GetAvailableOutput(stream));
                    if (memberWritten > remainingOutput.Length)
                        throw new CorruptParquetException("Gzip payload exceeds the declared uncompressed size.");
                    written += memberWritten;
                    var nextConsumed = input.Length - checked((int)ZlibNative.GetAvailableInput(stream));
                    if (nextConsumed <= consumed)
                        throw new CorruptParquetException("Gzip member consumed no compressed bytes.");
                    consumed = nextConsumed;
                }
                finally
                {
                    ZlibNative.InflateEnd(stream);
                }
            }
        } while (consumed < input.Length);
        return written;
    }
}
