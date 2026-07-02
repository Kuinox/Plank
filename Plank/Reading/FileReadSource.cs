using Microsoft.Win32.SafeHandles;

namespace Plank.Reading;

/// <summary>
/// Provides random-access reads over a file-backed Parquet source without allocating a <see cref="FileStream"/>.
/// </summary>
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

    public ulong Length
        => (ulong)_length;

    public void ReadExactly(ulong offset, Span<byte> destination)
    {
        if (offset > (ulong)_length - (ulong)destination.Length)
            throw new CorruptParquetException($"Attempted to read {destination.Length} bytes at offset {offset} but the source is only {_length} bytes long.");

        var signedOffset = (long)offset;
        while (!destination.IsEmpty)
        {
            var read = RandomAccess.Read(_handle, destination, signedOffset);
            if (read == 0)
                throw new EndOfStreamException();

            destination = destination[read..];
            signedOffset += read;
        }
    }

    public void Dispose()
        => _handle.Dispose();
}
