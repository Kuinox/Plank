using System.Text;
using Plank.Reading;
using Plank.Writing;

namespace Plank.Fuzzing;

/// <summary>
/// A shared set of in-memory files, addressed by path exactly as the dataset
/// writer addresses real ones.
/// </summary>
/// <remarks>
/// The dataset writer is the only part of the write side that owns files rather
/// than being handed a stream: it routes each row to a partition, names a file
/// per partition, and opens, appends to and closes them itself. That is why it
/// could not be fuzzed — a fuzzer must not touch the filesystem tens of
/// thousands of times a second — and why DatasetWriterBase measured 0/455.
///
/// Backing the paths with memory keeps the writer's own logic under test (path
/// construction, partition bookkeeping, append vs create, the file pool) while
/// removing the only reason not to run it.
/// </remarks>
sealed class InMemoryFileStore
{
    readonly Dictionary<string, MemoryStream> _files = [];

    internal MemoryStream Resolve(ReadOnlySpan<byte> path, FileMode mode)
    {
        var key = Encoding.UTF8.GetString(path);
        if (!_files.TryGetValue(key, out var file))
        {
            file = new MemoryStream();
            _files[key] = file;
        }

        // Create truncates; OpenOrCreate keeps what is there, which is how the
        // writer appends a second row group to a partition it has already
        // written. Those are the only two modes it asks for.
        if (mode == FileMode.Create)
            file.SetLength(0);
        return file;
    }

    internal IReadOnlyDictionary<string, MemoryStream> Files
        => _files;
}

/// <summary>One slot in the dataset writer's file pool, backed by the store.</summary>
sealed class InMemoryDatasetFile(InMemoryFileStore store) : IParquetReadSource, IParquetWriteSource
{
    MemoryStream? _current;

    MemoryStream Current
        => _current ?? throw new InvalidOperationException("No in-memory destination is open.");

    public void Open(ReadOnlySpan<byte> path, FileMode mode)
        => _current = store.Resolve(path, mode);

    public void Close()
        => _current = null;

    public void Write(ulong offset, ReadOnlySpan<byte> source)
    {
        var stream = Current;
        var end = offset + (ulong)source.Length;
        if (end > int.MaxValue)
            throw new NotSupportedException("In-memory destinations larger than Int32.MaxValue are not supported.");
        if ((ulong)stream.Length < end)
            stream.SetLength((long)end);
        stream.Position = (long)offset;
        stream.Write(source);
    }

    public void SetLength(ulong length)
    {
        if (length > int.MaxValue)
            throw new NotSupportedException("In-memory destinations larger than Int32.MaxValue are not supported.");
        Current.SetLength((long)length);
    }

    public void Flush()
    {
    }

    public ulong Length
        => (ulong)Current.Length;

    public void ReadExactly(ulong offset, Span<byte> destination)
    {
        var stream = Current;
        var length = (ulong)stream.Length;
        if (offset > length || (ulong)destination.Length > length - offset)
            throw new CorruptParquetException(
                $"Attempted to read {destination.Length} bytes at offset {offset} but the destination is only {length} bytes long.");
        stream.Position = (long)offset;
        stream.ReadExactly(destination);
    }

    // The pool owns the slot, not the files: closing a slot must not discard a
    // partition that another slot, or the verification pass, still needs.
    public void Dispose()
        => _current = null;
}
