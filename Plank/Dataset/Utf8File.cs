using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Plank.Dataset;

static unsafe class Utf8File
{
    static readonly Encoding _strictUtf8 = new UTF8Encoding(false, true);

    const uint GenericRead = 0x80000000;
    const uint GenericWrite = 0x40000000;
    const uint OpenAlways = 4;
    const uint FileAttributeNormal = 0x80;
    const int UnixReadWrite = 2;
    const int UnixCreateLinux = 0x40;
    const int UnixCreateMacOs = 0x200;
    const int UnixCreateMode = 0x1B6;

    internal static Stream Open(ReadOnlySpan<byte> path, IParquetBufferPool bufferPool)
    {
        if (path.IsEmpty)
            throw new ArgumentException("A dataset file path must not be empty.", nameof(path));
        ArgumentNullException.ThrowIfNull(bufferPool);
        _ = _strictUtf8.GetCharCount(path);

        return OperatingSystem.IsWindows()
            ? OpenWindows(path, bufferPool)
            : OpenUnix(path, bufferPool);
    }

    static Stream OpenWindows(ReadOnlySpan<byte> path, IParquetBufferPool bufferPool)
    {
        var requiredChars = checked(path.Length + 1);
        ParquetBuffer allocation = default;
        Span<char> utf16 = requiredChars <= 512
            ? stackalloc char[requiredChars]
            : RentChars(bufferPool, requiredChars, out allocation);

        try
        {
            var charsWritten = _strictUtf8.GetChars(path, utf16);
            utf16[charsWritten] = '\0';
            fixed (char* pathPointer = utf16)
            {
                var handle = CreateFileW(pathPointer, GenericRead | GenericWrite, 0, null, OpenAlways,
                    FileAttributeNormal, 0);
                if (handle.IsInvalid)
                {
                    var error = Marshal.GetLastPInvokeError();
                    handle.Dispose();
                    throw new Win32Exception(error, "The dataset file could not be opened.");
                }

                return new FileStream(handle, FileAccess.ReadWrite, 1, false);
            }
        }
        finally
        {
            allocation.Dispose();
        }
    }

    static Stream OpenUnix(ReadOnlySpan<byte> path, IParquetBufferPool bufferPool)
    {
        var requiredBytes = checked(path.Length + 1);
        ParquetBuffer allocation = default;
        Span<byte> terminatedPath = requiredBytes <= 512
            ? stackalloc byte[requiredBytes]
            : RentBytes(bufferPool, requiredBytes, out allocation);

        try
        {
            path.CopyTo(terminatedPath);
            terminatedPath[path.Length] = 0;
            var createFlag = OperatingSystem.IsMacOS() ? UnixCreateMacOs : UnixCreateLinux;
            fixed (byte* pathPointer = terminatedPath)
            {
                var descriptor = OpenUnixFile(pathPointer, UnixReadWrite | createFlag, UnixCreateMode);
                if (descriptor < 0)
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "The dataset file could not be opened.");

                return new FileStream(new SafeFileHandle((nint)descriptor, ownsHandle: true), FileAccess.ReadWrite, 1,
                    false);
            }
        }
        finally
        {
            allocation.Dispose();
        }
    }

    static Span<char> RentChars(IParquetBufferPool bufferPool, int charCount, out ParquetBuffer allocation)
    {
        allocation = bufferPool.Rent(checked((uint)(charCount * sizeof(char))));
        return MemoryMarshal.Cast<byte, char>(allocation.Span)[..charCount];
    }

    static Span<byte> RentBytes(IParquetBufferPool bufferPool, int byteCount, out ParquetBuffer allocation)
    {
        allocation = bufferPool.Rent(checked((uint)byteCount));
        return allocation.Span[..byteCount];
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true)]
    static extern SafeFileHandle CreateFileW(char* fileName, uint desiredAccess, uint shareMode,
        void* securityAttributes, uint creationDisposition, uint flagsAndAttributes, nint templateFile);

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    static extern int OpenUnixFile(byte* path, int flags, int mode);
}
