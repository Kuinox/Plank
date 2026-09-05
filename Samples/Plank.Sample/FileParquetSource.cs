using Plank.Writing;

namespace Plank.Sample;

#region FileParquetSource
// A reusable local-file adapter for dataset writing. One instance owns one open file.
sealed class FileParquetSource : IParquetReadWriteSource
{
    FileStream? _stream;

    FileStream Stream => _stream ?? throw new InvalidOperationException("The file is not open.");

    public ulong Length => checked((ulong)Stream.Length);

    public void Open(ReadOnlySpan<byte> path, FileMode mode)
    {
        if (_stream is not null)
            throw new InvalidOperationException("The previous file must be closed before this source is reused.");
        var filePath = Encoding.UTF8.GetString(path);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(filePath))!);
        _stream = new FileStream(filePath, mode, FileAccess.ReadWrite, FileShare.None);
    }

    public void ReadExactly(ulong offset, Span<byte> destination)
    {
        Stream.Position = checked((long)offset);
        Stream.ReadExactly(destination);
    }

    public void Write(ulong offset, ReadOnlySpan<byte> source)
    {
        Stream.Position = checked((long)offset);
        Stream.Write(source);
    }

    public void SetLength(ulong length) => Stream.SetLength(checked((long)length));

    public void Flush() => Stream.Flush();

    public void Close()
    {
        _stream?.Dispose();
        _stream = null;
    }

    public void Dispose() => Close();
}
#endregion
