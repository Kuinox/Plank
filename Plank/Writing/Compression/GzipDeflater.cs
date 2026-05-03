namespace Plank.Writing.Compression;

unsafe sealed class GzipDeflater
{
    readonly void* _stream;
    bool _initialized;

    internal GzipDeflater()
    {
        _stream = System.Runtime.InteropServices.NativeMemory.AllocZeroed((nuint)ZlibNative.StreamStateSize);
        if (_stream is null)
            throw new OutOfMemoryException("Failed to allocate zlib stream state.");
    }

    ~GzipDeflater()
    {
        if (_initialized)
            ZlibNative.DeflateEnd(_stream);

        System.Runtime.InteropServices.NativeMemory.Free(_stream);
    }

    internal void Compress(ReadOnlySpan<byte> input, byte[] outputBuffer, ref BufferWriter destination)
    {
        EnsureInitialized();

        var resetCode = ZlibNative.DeflateReset(_stream);
        if (resetCode != ZlibNative.ResultOk)
            throw new InvalidOperationException($"zlib deflateReset failed with code {resetCode}.");

        fixed (byte* output = outputBuffer)
        {
            if (input.Length == 0)
            {
                DeflateInput(_stream, null, 0, output, outputBuffer, ref destination);
                return;
            }

            fixed (byte* inputStart = input)
                DeflateInput(_stream, inputStart, input.Length, output, outputBuffer, ref destination);
        }
    }

    void EnsureInitialized()
    {
        if (_initialized)
            return;

        var version = ZlibNative.GetVersion();
        var initCode = ZlibNative.DeflateInit2(_stream, ZlibNative.CompressionLevelFast, ZlibNative.CompressionMethodDeflate,
            ZlibNative.WindowBitsGzip, ZlibNative.MemoryLevelDefault, ZlibNative.CompressionStrategyDefault, (byte*)version,
            ZlibNative.StreamStateSize);
        if (initCode != ZlibNative.ResultOk)
            throw new InvalidOperationException($"zlib deflateInit2_ failed with code {initCode}.");

        _initialized = true;
    }

    static void DeflateInput(void* stream, byte* input, int inputLength, byte* output, byte[] outputBuffer,
        ref BufferWriter destination)
    {
        ZlibNative.SetInput(stream, input, inputLength);

        while (true)
        {
            ZlibNative.SetOutput(stream, output, outputBuffer.Length);

            var resultCode = ZlibNative.Deflate(stream, ZlibNative.FlushFinish);
            var written = outputBuffer.Length - checked((int)ZlibNative.GetAvailableOutput(stream));
            if (written > 0)
                destination.Write(outputBuffer.AsSpan(0, written));

            if (resultCode == ZlibNative.ResultStreamEnd)
                return;
            if (resultCode != ZlibNative.ResultOk)
                throw new InvalidOperationException($"zlib deflate failed with code {resultCode}.");
        }
    }
}
