using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Plank.IO.ZeroAlloc;

public sealed unsafe class ReusableFileWriteStream : Stream
{
    static readonly Encoding _strictUtf8 = new UTF8Encoding(false, true);

    const uint GenericRead = 0x80000000;
    const uint GenericWrite = 0x40000000;
    const uint FileAttributeNormal = 0x80;
    const int UnixReadOnly = 0;
    const int UnixWriteOnly = 1;
    const int UnixReadWrite = 2;
    const int UnixCreateLinux = 0x40;
    const int UnixExclusiveLinux = 0x80;
    const int UnixTruncateLinux = 0x200;
    const int UnixCloseOnExecLinux = 0x80000;
    const int UnixCreateMacOs = 0x200;
    const int UnixExclusiveMacOs = 0x800;
    const int UnixTruncateMacOs = 0x400;
    const int UnixCloseOnExecMacOs = 0x1000000;
    const int UnixCreateMode = 0x1B6;

    SafeFileHandle? _handle;
    FileAccess _access;
    long _position;

    public override bool CanRead
        => IsOpen && (_access & FileAccess.Read) != 0;

    public override bool CanSeek
        => IsOpen;

    public override bool CanWrite
        => IsOpen && (_access & FileAccess.Write) != 0;

    public override long Length
        => RandomAccess.GetLength(GetOpenHandle());

    public override long Position
    {
        get
        {
            _ = GetOpenHandle();
            return _position;
        }
        set
        {
            _ = GetOpenHandle();
            if (value < 0)
                throw new ArgumentOutOfRangeException(nameof(value), value, "Position must be non-negative.");
            _position = value;
        }
    }

    bool IsOpen
        => _handle is { IsClosed: false, IsInvalid: false };

    public void Open(string path)
        => Open(path, FileMode.Create, FileAccess.Write);

    public void Open(string path, FileMode mode, FileAccess access)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ValidateOpen(mode, access);
        ThrowIfOpen();

        var handle = File.OpenHandle(path, mode, access, FileShare.None, FileOptions.None);
        OpenHandle(handle, mode, access);
    }

    public void Open(ReadOnlySpan<byte> utf8Path)
        => Open(utf8Path, FileMode.Create, FileAccess.Write);

    public void Open(ReadOnlySpan<byte> utf8Path, FileMode mode, FileAccess access)
    {
        if (utf8Path.IsEmpty)
            throw new ArgumentException("A file path must not be empty.", nameof(utf8Path));
        if (utf8Path.IndexOf((byte)0) >= 0)
            throw new ArgumentException("A file path must not contain a null byte.", nameof(utf8Path));
        ValidateOpen(mode, access);
        ThrowIfOpen();
        _ = _strictUtf8.GetCharCount(utf8Path);

        var handle = OperatingSystem.IsWindows()
            ? OpenWindows(utf8Path, mode, access)
            : OpenUnix(utf8Path, mode, access);
        OpenHandle(handle, mode, access);
    }

    public void CloseFile()
    {
        _handle?.Dispose();
        _handle = null;
        _access = default;
        _position = 0;
    }

    public override void Flush()
        => RandomAccess.FlushToDisk(GetOpenHandle());

    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Flush();
        return Task.CompletedTask;
    }

    public override int Read(byte[] buffer, int offset, int count)
        => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var count = RandomAccess.Read(GetReadableHandle(), buffer, _position);
        _position += count;
        return count;
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Read(buffer.Span));
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        _ = GetOpenHandle();
        var position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => checked(_position + offset),
            SeekOrigin.End => checked(Length + offset),
            _ => throw new ArgumentOutOfRangeException(nameof(origin), origin, "Seek origin is not valid.")
        };
        if (position < 0)
            throw new IOException("The seek operation moved before the start of the file.");

        _position = position;
        return position;
    }

    public override void SetLength(long value)
    {
        if (value < 0)
            throw new ArgumentOutOfRangeException(nameof(value), value, "Length must be non-negative.");
        RandomAccess.SetLength(GetWritableHandle(), value);
    }

    public override void Write(byte[] buffer, int offset, int count)
        => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        RandomAccess.Write(GetWritableHandle(), buffer, _position);
        _position += buffer.Length;
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Write(buffer, offset, count);
        return Task.CompletedTask;
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Write(buffer.Span);
        return ValueTask.CompletedTask;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            CloseFile();

        base.Dispose(disposing);
    }

    void OpenHandle(SafeFileHandle handle, FileMode mode, FileAccess access)
    {
        try
        {
            _position = mode == FileMode.Append ? RandomAccess.GetLength(handle) : 0;
            _access = access;
            _handle = handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    void ThrowIfOpen()
    {
        if (IsOpen)
            throw new InvalidOperationException("A file is already open. Call CloseFile() first.");
    }

    SafeFileHandle GetOpenHandle()
        => IsOpen
            ? _handle!
            : throw new InvalidOperationException("No file is open. Call Open(path) first.");

    SafeFileHandle GetReadableHandle()
    {
        var handle = GetOpenHandle();
        return CanRead ? handle : throw new NotSupportedException("The open file does not allow reads.");
    }

    SafeFileHandle GetWritableHandle()
    {
        var handle = GetOpenHandle();
        return CanWrite ? handle : throw new NotSupportedException("The open file does not allow writes.");
    }

    static void ValidateOpen(FileMode mode, FileAccess access)
    {
        if (!Enum.IsDefined(mode))
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "File mode is not valid.");
        if (!Enum.IsDefined(access))
            throw new ArgumentOutOfRangeException(nameof(access), access, "File access is not valid.");
        if (mode == FileMode.Append && access != FileAccess.Write)
            throw new ArgumentException("Append mode requires write-only access.", nameof(access));
        if (access == FileAccess.Read && mode is FileMode.CreateNew or FileMode.Create or FileMode.Truncate)
            throw new ArgumentException("This file mode requires write access.", nameof(access));
    }

    static SafeFileHandle OpenWindows(ReadOnlySpan<byte> utf8Path, FileMode mode, FileAccess access)
    {
        var charCount = _strictUtf8.GetCharCount(utf8Path);
        if (charCount >= short.MaxValue)
            throw new PathTooLongException("The UTF-8 file path is too long.");

        Span<char> utf16Path = stackalloc char[charCount + 1];
        var charsWritten = _strictUtf8.GetChars(utf8Path, utf16Path);
        utf16Path[charsWritten] = '\0';
        fixed (char* pathPointer = utf16Path)
        {
            var desiredAccess = access switch
            {
                FileAccess.Read => GenericRead,
                FileAccess.Write => GenericWrite,
                FileAccess.ReadWrite => GenericRead | GenericWrite,
                _ => 0u
            };
            var creationDisposition = mode == FileMode.Append ? (uint)FileMode.OpenOrCreate : (uint)mode;
            var handle = CreateFileW(pathPointer, desiredAccess, 0, null, creationDisposition,
                FileAttributeNormal, 0);
            if (!handle.IsInvalid)
                return handle;

            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new Win32Exception(error, "The file could not be opened.");
        }
    }

    static SafeFileHandle OpenUnix(ReadOnlySpan<byte> utf8Path, FileMode mode, FileAccess access)
    {
        if (utf8Path.Length >= short.MaxValue)
            throw new PathTooLongException("The UTF-8 file path is too long.");

        Span<byte> terminatedPath = stackalloc byte[utf8Path.Length + 1];
        utf8Path.CopyTo(terminatedPath);
        terminatedPath[utf8Path.Length] = 0;
        var flags = GetUnixAccess(access) | GetUnixMode(mode);
        fixed (byte* pathPointer = terminatedPath)
        {
            var descriptor = OpenUnixFile(pathPointer, flags, UnixCreateMode);
            if (descriptor >= 0)
                return new SafeFileHandle((nint)descriptor, ownsHandle: true);

            throw new Win32Exception(Marshal.GetLastPInvokeError(), "The file could not be opened.");
        }
    }

    static int GetUnixAccess(FileAccess access)
        => access switch
        {
            FileAccess.Read => UnixReadOnly,
            FileAccess.Write => UnixWriteOnly,
            FileAccess.ReadWrite => UnixReadWrite,
            _ => 0
        };

    static int GetUnixMode(FileMode mode)
    {
        var create = OperatingSystem.IsMacOS() ? UnixCreateMacOs : UnixCreateLinux;
        var exclusive = OperatingSystem.IsMacOS() ? UnixExclusiveMacOs : UnixExclusiveLinux;
        var truncate = OperatingSystem.IsMacOS() ? UnixTruncateMacOs : UnixTruncateLinux;
        var closeOnExec = OperatingSystem.IsMacOS() ? UnixCloseOnExecMacOs : UnixCloseOnExecLinux;
        return closeOnExec | (mode switch
        {
            FileMode.CreateNew => create | exclusive,
            FileMode.Create => create | truncate,
            FileMode.Open => 0,
            FileMode.OpenOrCreate => create,
            FileMode.Truncate => truncate,
            FileMode.Append => create,
            _ => 0
        });
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true)]
    static extern SafeFileHandle CreateFileW(char* fileName, uint desiredAccess, uint shareMode,
        void* securityAttributes, uint creationDisposition, uint flagsAndAttributes, nint templateFile);

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    static extern int OpenUnixFile(byte* path, int flags, int mode);
}
