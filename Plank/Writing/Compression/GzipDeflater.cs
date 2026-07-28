using Plank.Internal.Compression;

namespace Plank.Writing.Compression;

unsafe static class GzipDeflater
{
    internal static void Compress(int compressionLevel, ReadOnlySpan<byte> input, Span<byte> outputBuffer,
        ref BufferWriter destination)
    {
        Span<byte> streamState = stackalloc byte[ZlibNative.StreamStateSize];
        streamState.Clear();
        fixed (byte* stream = streamState)
        fixed (byte* output = outputBuffer)
        {
            var version = ZlibNative.GetVersion();
            var initCode = ZlibNative.DeflateInit2(stream, compressionLevel, ZlibNative.CompressionMethodDeflate,
                ZlibNative.WindowBitsGzip, ZlibNative.MemoryLevelDefault, ZlibNative.CompressionStrategyDefault,
                (byte*)version, ZlibNative.StreamStateSize);
            if (initCode != ZlibNative.ResultOk)
                throw new InvalidOperationException($"zlib deflateInit2_ failed with code {initCode}.");

            try
            {
                if (input.IsEmpty)
                {
                    DeflateInput(stream, null, 0, output, outputBuffer.Length, ref destination);
                    return;
                }

                fixed (byte* inputStart = input)
                    DeflateInput(stream, inputStart, input.Length, output, outputBuffer.Length, ref destination);
            }
            finally
            {
                ZlibNative.DeflateEnd(stream);
            }
        }
    }

    static void DeflateInput(void* stream, byte* input, int inputLength, byte* output, int outputLength,
        ref BufferWriter destination)
    {
        ZlibNative.SetInput(stream, input, inputLength);

        while (true)
        {
            ZlibNative.SetOutput(stream, output, outputLength);

            var resultCode = ZlibNative.Deflate(stream, ZlibNative.FlushFinish);
            var written = outputLength - checked((int)ZlibNative.GetAvailableOutput(stream));
            if (written > 0)
                destination.Write(new ReadOnlySpan<byte>(output, written));

            if (resultCode == ZlibNative.ResultStreamEnd)
                return;
            if (resultCode != ZlibNative.ResultOk)
                throw new InvalidOperationException($"zlib deflate failed with code {resultCode}.");
        }
    }
}
