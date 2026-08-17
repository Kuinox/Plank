using System.Collections.Immutable;
using Plank.Reading;
using Plank.Schema;
using Plank.Writing;

namespace Plank.Fuzzing;

/// <summary>
/// Writes several small files, merges them, and checks the merged file still
/// holds every value in order.
/// </summary>
/// <remarks>
/// ParquetFileMerger measured 0/74 lines, and it is a better fuzz target than
/// its size suggests: it is the one write path that does not re-encode. It reads
/// each source file's metadata, splices the column chunks across verbatim, and
/// rewrites the offsets to match their new positions. That is offset arithmetic
/// over lengths taken from a file, which is exactly the shape of code worth
/// pointing a fuzzer at.
///
/// The oracle is concatenation: merging files holding A then B must produce
/// exactly A followed by B. A merger that miscounted a row group, dropped a
/// chunk or wrote a stale offset fails that, and fails it loudly rather than by
/// crashing.
/// </remarks>
public static class PlankMergerFuzzTarget
{
    const int MaxFiles = 4;
    const int MaxRowsPerFile = 32;

    public static void Execute(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2)
            return;

        try
        {
            Run(data);
        }
        catch (Exception ex) when (ex is CorruptParquetException or NotSupportedException
            or InvalidOperationException or ArgumentException)
        {
            // ArgumentException is expected here as well as the usual set: the
            // merger rejects a destination that is also a source, and rejects
            // files whose schema does not match the one it was created with.
        }
    }

    static void Run(ReadOnlySpan<byte> data)
    {
        var cursor = new Cursor(data);
        var fileCount = cursor.NextInt(1, MaxFiles + 1);

        // One schema for every file: the merger requires them to match, and a
        // mismatch is rejected long before any of the splicing runs.
        var schema = new ParquetSchema([ColumnDefinition.Leaf("c", ParquetPhysicalType.Int32,
            new ColumnOptions(ParquetRepetition.Required,
                encodings: ImmutableArray.Create(EncodingKind.Plain)))]);

        var files = new byte[fileCount][];
        var expected = new List<int>();
        for (var i = 0; i < fileCount; i++)
        {
            var rows = cursor.NextInt(1, MaxRowsPerFile + 1);
            var values = new int[rows];
            for (var r = 0; r < rows; r++)
                values[r] = cursor.NextInt32();
            expected.AddRange(values);
            // Codecs vary per file on purpose: the merger copies chunks without
            // re-encoding, so a merged file legitimately holds several codecs at
            // once and has to record each chunk's own.
            files[i] = WriteFile(schema, values, PickCompression(ref cursor));
        }

        using var destination = new MemoryReadWriteSource();
        var options = new ParquetMergeOptions
        {
            PreserveFirstFileMetadata = cursor.NextInt(0, 2) == 0
        };

        // Two ways in, and they take different constructors. The two-argument
        // form starts an empty destination from a first source file. The
        // one-argument form appends to a destination that already holds a file,
        // which is the only path through ParquetAppendOptions and the only one
        // that has to read back existing footer counts before writing.
        ParquetFileMerger merger;
        var appendToExisting = cursor.NextInt(0, 2) == 0;
        if (appendToExisting)
        {
            destination.Write(0, files[0]);
            merger = schema.CreateMerger(destination, options);
        }
        else
        {
            merger = schema.CreateMerger(new MemoryReadSource(files[0]), destination, options);
        }

        for (var i = 1; i < fileCount; i++)
            merger.AppendFile(new MemoryReadSource(files[i]));

        if (merger.SourceFileCount != fileCount)
            throw new MergeException($"Merger counted {merger.SourceFileCount} source files, expected {fileCount}.");
        if (merger.RowCount != expected.Count)
            throw new MergeException($"Merger counted {merger.RowCount} rows, expected {expected.Count}.");
        merger.CloseFile();

        VerifyConcatenation(schema, destination.ToArray(), expected);
    }

    static void VerifyConcatenation(ParquetSchema schema, byte[] merged, List<int> expected)
    {
        var actual = new List<int>();
        using var reader = schema.CreateReader(new MemoryStream(merged, writable: false));
        foreach (var rowGroup in reader.RowGroups)
            foreach (var buffer in rowGroup.Column<int>(schema.LeafColumns[0]))
                foreach (var value in buffer.Values)
                    actual.Add(value);

        if (actual.Count != expected.Count)
            throw new MergeException($"Merged file holds {actual.Count} values, expected {expected.Count}.");
        for (var i = 0; i < actual.Count; i++)
            if (actual[i] != expected[i])
                throw new MergeException(
                    $"Merged value {i} is {actual[i]}, expected {expected[i]} — the merge did not concatenate in order.");
    }

    static byte[] WriteFile(ParquetSchema schema, int[] values, CompressionKind compression)
    {
        using var stream = new MemoryStream();
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions { Compression = compression });
        var group = writer.StartRowGroup();
        var column = writer.CreateSerializedColumn<int>(schema.LeafColumns[0]);
        column.Serialize(values);
        group.Write(column);
        writer.CloseFile();
        return stream.ToArray();
    }

    static CompressionKind PickCompression(ref Cursor cursor)
        => cursor.NextInt(0, 4) switch
        {
            0 => CompressionKind.None,
            1 => CompressionKind.Snappy,
            2 => CompressionKind.Gzip,
            _ => CompressionKind.Zstd
        };

    /// <summary>Signals that a merge lost, reordered or duplicated values.</summary>
    public sealed class MergeException(string message) : Exception(message);

    /// <summary>An in-memory destination the merger can both read and write.</summary>
    sealed class MemoryReadWriteSource : IParquetReadWriteSource
    {
        readonly MemoryStream _stream = new();

        public ulong Length
            => (ulong)_stream.Length;

        public void Open(ReadOnlySpan<byte> path, FileMode mode)
        {
            if (mode == FileMode.Create)
                _stream.SetLength(0);
        }

        public void Close()
        {
        }

        public void Write(ulong offset, ReadOnlySpan<byte> source)
        {
            var end = offset + (ulong)source.Length;
            if (end > int.MaxValue)
                throw new NotSupportedException("In-memory destinations larger than Int32.MaxValue are not supported.");
            if ((ulong)_stream.Length < end)
                _stream.SetLength((long)end);
            _stream.Position = (long)offset;
            _stream.Write(source);
        }

        public void SetLength(ulong length)
        {
            if (length > int.MaxValue)
                throw new NotSupportedException("In-memory destinations larger than Int32.MaxValue are not supported.");
            _stream.SetLength((long)length);
        }

        public void Flush()
        {
        }

        public void ReadExactly(ulong offset, Span<byte> destination)
        {
            var length = (ulong)_stream.Length;
            if (offset > length || (ulong)destination.Length > length - offset)
                throw new CorruptParquetException(
                    $"Attempted to read {destination.Length} bytes at offset {offset} but the destination is only {length} bytes long.");
            _stream.Position = (long)offset;
            _stream.ReadExactly(destination);
        }

        internal byte[] ToArray()
            => _stream.ToArray();

        public void Dispose()
            => _stream.Dispose();
    }

    ref struct Cursor(ReadOnlySpan<byte> data)
    {
        readonly ReadOnlySpan<byte> _data = data;
        int _offset;

        byte NextByte()
            => _offset < _data.Length ? _data[_offset++] : (byte)0;

        internal int NextInt(int minInclusive, int maxExclusive)
        {
            var range = maxExclusive - minInclusive;
            return range <= 0 ? minInclusive : minInclusive + (NextByte() % range);
        }

        internal int NextInt32()
            => (NextByte() << 24) | (NextByte() << 16) | (NextByte() << 8) | NextByte();
    }
}
