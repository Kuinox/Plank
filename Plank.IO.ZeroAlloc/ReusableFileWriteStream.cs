using Microsoft.Win32.SafeHandles;

namespace Plank.IO.ZeroAlloc;

public sealed class ReusableFileWriteStream : Stream
{
    SafeFileHandle? _handle;
    long _position;

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite
        => _handle is { IsClosed: false, IsInvalid: false };

    public override long Length
        => throw new NotSupportedException();

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public void Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (CanWrite)
            throw new InvalidOperationException("A file is already open. Call CloseFile() first.");

        _handle = File.OpenHandle(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            FileOptions.None);
        _position = 0;
    }

    public void CloseFile()
    {
        _handle?.Dispose();
        _handle = null;
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
        => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin)
        => throw new NotSupportedException();

    public override void SetLength(long value)
        => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
        => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        var handle = GetOpenHandle();
        RandomAccess.Write(handle, buffer, _position);
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

    SafeFileHandle GetOpenHandle()
        => CanWrite
            ? _handle!
            : throw new InvalidOperationException("No file is open. Call Open(path) first.");
}
