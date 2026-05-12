using Microsoft.Win32.SafeHandles;

namespace Plank.Reading;

public sealed class FileReadSource : IParquetReadSource, IDisposable
{
    readonly SafeFileHandle _handle;
    readonly long _length;

    public FileReadSource(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);
        _length = RandomAccess.GetLength(_handle);
    }

    public long Length
        => _length;

    public void ReadExactly(long offset, Span<byte> destination)
    {
        if (offset < 0 || offset > _length - destination.Length)
            throw new ArgumentOutOfRangeException(nameof(offset), offset, "Read range is outside the source.");

        while (!destination.IsEmpty)
        {
            var read = RandomAccess.Read(_handle, destination, offset);
            if (read == 0)
                throw new EndOfStreamException();

            destination = destination[read..];
            offset += read;
        }
    }

    public void Dispose()
        => _handle.Dispose();
}
