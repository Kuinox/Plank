using System.Runtime.InteropServices;

namespace Plank.Writing.Compression;

unsafe static partial class ZlibNative
{
    internal const string LibraryName = "z";
    internal const int CompressionMethodDeflate = 8;
    internal const int CompressionStrategyDefault = 0;
    internal const int CompressionLevelFast = 1;
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

    // Replaced LibraryImport (source-generated) with DllImport (static) for Stryker worktree
    // — LibraryImportGenerator requires .NET 10 Immutable Collections which Stryker's Roslyn can't load.
    [DllImport(LibraryName, EntryPoint = "zlibVersion")]
    internal static extern IntPtr GetVersion();

    [DllImport(LibraryName, EntryPoint = "deflateInit2_")]
    internal static extern int DeflateInit2(void* stream, int level, int method, int windowBits, int memoryLevel,
        int strategy, byte* version, int streamSize);

    [DllImport(LibraryName, EntryPoint = "deflate")]
    internal static extern int Deflate(void* stream, int flushMode);

    [DllImport(LibraryName, EntryPoint = "deflateReset")]
    internal static extern int DeflateReset(void* stream);

    [DllImport(LibraryName, EntryPoint = "deflateEnd")]
    internal static extern int DeflateEnd(void* stream);
}
