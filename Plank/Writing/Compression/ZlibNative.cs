using System.Runtime.InteropServices;

namespace Plank.Writing.Compression;

unsafe static partial class ZlibNative
{
    internal const int CompressionMethodDeflate = 8;
    internal const int CompressionStrategyDefault = 0;
    internal const int CompressionLevelFast = 1;
    internal const int FlushFinish = 4;
    internal const int ResultOk = 0;
    internal const int ResultStreamEnd = 1;
    internal const int WindowBitsGzip = 31;
    internal const int MemoryLevelDefault = 8;

    [StructLayout(LayoutKind.Sequential)]
    internal struct StreamState
    {
        internal byte* NextInput;
        internal uint AvailableInput;
        internal nuint TotalInput;
        internal byte* NextOutput;
        internal uint AvailableOutput;
        internal nuint TotalOutput;
        internal IntPtr Message;
        internal IntPtr InternalState;
        internal IntPtr Allocate;
        internal IntPtr Free;
        internal IntPtr Opaque;
        internal int DataType;
        internal nuint Adler;
        internal nuint Reserved;
    }

    [LibraryImport("z", EntryPoint = "zlibVersion")]
    internal static partial IntPtr GetVersion();

    [LibraryImport("z", EntryPoint = "deflateInit2_")]
    internal static partial int DeflateInit2(StreamState* stream, int level, int method, int windowBits, int memoryLevel,
        int strategy, byte* version, int streamSize);

    [LibraryImport("z", EntryPoint = "deflate")]
    internal static partial int Deflate(StreamState* stream, int flushMode);

    [LibraryImport("z", EntryPoint = "deflateReset")]
    internal static partial int DeflateReset(StreamState* stream);

    [LibraryImport("z", EntryPoint = "deflateEnd")]
    internal static partial int DeflateEnd(StreamState* stream);
}
