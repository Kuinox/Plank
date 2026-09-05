using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Plank.Internal.Compression;

unsafe static partial class ZlibNative
{
    internal const string LibraryName = "z";
    internal const int CompressionMethodDeflate = 8;
    internal const int CompressionStrategyDefault = 0;
    internal const int FlushFinish = 4;
    internal const int ResultOk = 0;
    internal const int ResultStreamEnd = 1;
    internal const int WindowBitsGzip = 31;
    internal const int MemoryLevelDefault = 8;

    static ZlibNative()
        => ZlibLibraryResolver.Register();

    internal static int StreamStateSize
        => OperatingSystem.IsWindows() ? sizeof(WindowsStreamState) : sizeof(UnixStreamState);

    internal static void SetInput(void* stream, byte* input, int inputLength)
    {
        if (OperatingSystem.IsWindows())
        {
            var state = (WindowsStreamState*)stream;
            state->NextInput = input;
            state->AvailableInput = checked((uint)inputLength);
            return;
        }

        var unixState = (UnixStreamState*)stream;
        unixState->NextInput = input;
        unixState->AvailableInput = checked((uint)inputLength);
    }

    internal static void SetOutput(void* stream, byte* output, int outputLength)
    {
        if (OperatingSystem.IsWindows())
        {
            var state = (WindowsStreamState*)stream;
            state->NextOutput = output;
            state->AvailableOutput = checked((uint)outputLength);
            return;
        }

        var unixState = (UnixStreamState*)stream;
        unixState->NextOutput = output;
        unixState->AvailableOutput = checked((uint)outputLength);
    }

    internal static uint GetAvailableOutput(void* stream)
    {
        if (OperatingSystem.IsWindows())
            return ((WindowsStreamState*)stream)->AvailableOutput;

        return ((UnixStreamState*)stream)->AvailableOutput;
    }

    internal static uint GetAvailableInput(void* stream)
    {
        if (OperatingSystem.IsWindows())
            return ((WindowsStreamState*)stream)->AvailableInput;

        return ((UnixStreamState*)stream)->AvailableInput;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct UnixStreamState
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

    [StructLayout(LayoutKind.Sequential)]
    struct WindowsStreamState
    {
        internal byte* NextInput;
        internal uint AvailableInput;
        internal uint TotalInput;
        internal byte* NextOutput;
        internal uint AvailableOutput;
        internal uint TotalOutput;
        internal IntPtr Message;
        internal IntPtr InternalState;
        internal IntPtr Allocate;
        internal IntPtr Free;
        internal IntPtr Opaque;
        internal int DataType;
        internal uint Adler;
        internal uint Reserved;
    }

    // zlib uses the C ABI, including on Windows x86 where the default is Stdcall.
    [LibraryImport(LibraryName, EntryPoint = "zlibVersion")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial IntPtr GetVersion();

    [LibraryImport(LibraryName, EntryPoint = "deflateInit2_")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int DeflateInit2(void* stream, int level, int method, int windowBits, int memoryLevel,
        int strategy, byte* version, int streamSize);

    [LibraryImport(LibraryName, EntryPoint = "deflate")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int Deflate(void* stream, int flushMode);

    [LibraryImport(LibraryName, EntryPoint = "deflateEnd")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int DeflateEnd(void* stream);

    [LibraryImport(LibraryName, EntryPoint = "inflateInit2_")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int InflateInit2(void* stream, int windowBits, byte* version, int streamSize);

    [LibraryImport(LibraryName, EntryPoint = "inflate")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int Inflate(void* stream, int flushMode);

    [LibraryImport(LibraryName, EntryPoint = "inflateEnd")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int InflateEnd(void* stream);
}
