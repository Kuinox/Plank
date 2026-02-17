namespace Plank.Writing;

unsafe sealed class GzipDeflater
{
    ZlibNative.StreamState _stream;
    bool _initialized;

    ~GzipDeflater()
    {
        if (!_initialized)
            return;

        fixed (ZlibNative.StreamState* stream = &_stream)
            ZlibNative.DeflateEnd(stream);
    }

    internal void Compress(ArraySegment<byte> input, byte[] outputBuffer, ref BufferWriter destination)
    {
        fixed (ZlibNative.StreamState* stream = &_stream)
        {
            EnsureInitialized(stream);

            var resetCode = ZlibNative.DeflateReset(stream);
            if (resetCode != ZlibNative.ResultOk)
                throw new InvalidOperationException($"zlib deflateReset failed with code {resetCode}.");

            fixed (byte* output = outputBuffer)
            {
                if (input.Count == 0)
                    DeflateInput(stream, null, 0, output, outputBuffer, ref destination);
                else
                {
                    fixed (byte* inputStart = &input.Array![input.Offset])
                        DeflateInput(stream, inputStart, input.Count, output, outputBuffer, ref destination);
                }
            }
        }
    }

    void EnsureInitialized(ZlibNative.StreamState* stream)
    {
        if (_initialized)
            return;

        var version = ZlibNative.GetVersion();
        var initCode = ZlibNative.DeflateInit2(stream, ZlibNative.CompressionLevelFast, ZlibNative.CompressionMethodDeflate,
            ZlibNative.WindowBitsGzip, ZlibNative.MemoryLevelDefault, ZlibNative.CompressionStrategyDefault, (byte*)version,
            sizeof(ZlibNative.StreamState));
        if (initCode != ZlibNative.ResultOk)
            throw new InvalidOperationException($"zlib deflateInit2_ failed with code {initCode}.");

        _initialized = true;
    }

    static void DeflateInput(ZlibNative.StreamState* stream, byte* input, int inputLength, byte* output, byte[] outputBuffer,
        ref BufferWriter destination)
    {
        stream->NextInput = input;
        stream->AvailableInput = checked((uint)inputLength);

        while (true)
        {
            stream->NextOutput = output;
            stream->AvailableOutput = checked((uint)outputBuffer.Length);

            var resultCode = ZlibNative.Deflate(stream, ZlibNative.FlushFinish);
            var written = outputBuffer.Length - checked((int)stream->AvailableOutput);
            if (written > 0)
                destination.Write(outputBuffer.AsSpan(0, written));

            if (resultCode == ZlibNative.ResultStreamEnd)
                return;
            if (resultCode != ZlibNative.ResultOk)
                throw new InvalidOperationException($"zlib deflate failed with code {resultCode}.");
        }
    }
}
