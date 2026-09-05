using System.Text;
using Plank.Dataset;
using Plank.Reading;
using Plank.Schema;
using Plank.Writing;

namespace Plank.Tests.E2E;

internal sealed class DatasetNestedBinarySnapshotTests
{
    [Test]
    [Arguments(0)]
    [Arguments(3)]
    public void QueueSnapshotsNestedBinaryLeavesAndPreservesAbsentGroups(int pendingCapacity)
    {
        using var file = new MemoryDatasetFile();
        using (var writer = DatasetNestedBinarySnapshotRow.CreateDatasetWriter(Route, [file],
                   new DatasetWriterOptions { PendingRowCapacity = checked((uint)pendingCapacity) }))
        {
            var reused = new byte[4];
            for (var id = 0; id < 12; id++)
            {
                Array.Fill(reused, checked((byte)id));
                writer.Queue(new DatasetNestedBinarySnapshotRow
                {
                    Id = id,
                    Details = id % 2 == 0 ? null : new DatasetNestedBinaryDetails { Id = id, Payload = reused },
                    Items = (id % 3) switch { 0 => null, 1 => [], _ => [reused, null, []] },
                    Map = new Dictionary<int, byte[]?> { [1] = reused, [2] = null, [3] = [] },
                    Matrix = [[reused], [], [reused]],
                    Memories = [reused.AsMemory(1, 2), ReadOnlyMemory<byte>.Empty],
                    OptionalMemories = (id % 3) switch
                    {
                        0 => null,
                        1 => [],
                        _ => [reused.AsMemory(1, 2), (ReadOnlyMemory<byte>?)null, ReadOnlyMemory<byte>.Empty]
                    },
                    MemoryList = [reused.AsMemory(1, 2), ReadOnlyMemory<byte>.Empty],
                    OptionalMemoryList = (id % 3) switch
                    {
                        0 => null,
                        1 => [],
                        _ => [reused.AsMemory(1, 2), (ReadOnlyMemory<byte>?)null, ReadOnlyMemory<byte>.Empty]
                    },
                    MemoryMap = new Dictionary<int, ReadOnlyMemory<byte>?>
                    {
                        [1] = reused.AsMemory(1, 2), [2] = null, [3] = ReadOnlyMemory<byte>.Empty
                    }
                });
                Array.Fill(reused, byte.MaxValue);
            }
        }

        var seen = new HashSet<int>();
        foreach (var bytes in file.GetFiles())
        {
            using var stream = new MemoryStream(bytes);
            using var reader = DatasetNestedBinarySnapshotRow.CreateRowReader(stream);
            foreach (var row in reader)
            {
                var id = row.Id;
                if (!seen.Add(id))
                    throw new InvalidOperationException("A queued row was duplicated.");
                var details = row.Details;
                if (id % 2 == 0)
                {
                    if (details is not null)
                        throw new InvalidOperationException("An absent optional group became present.");
                }
                else
                {
                    if (details is null || details.Id != id)
                        throw new InvalidOperationException("A present optional group was lost.");
                    AssertPayload(details.Payload, id);
                }

                var items = row.Items;
                if (id % 3 == 0)
                {
                    if (items is not null)
                        throw new InvalidOperationException("A null list became non-null.");
                }
                else if (id % 3 == 1)
                {
                    if (items is not { Count: 0 })
                        throw new InvalidOperationException("An empty list changed.");
                }
                else
                {
                    if (items is not { Count: 3 } || items[1] is not null || items[2] is not { Length: 0 })
                        throw new InvalidOperationException("Binary list null or empty elements changed.");
                    AssertPayload(items[0], id);
                }

                var map = row.Map;
                if (map.Count != 3 || map[2] is not null || map[3] is not { Length: 0 })
                    throw new InvalidOperationException("Binary map null or empty values changed.");
                AssertPayload(map[1], id);

                var matrix = row.Matrix;
                if (matrix.Count != 3 || matrix[0].Count != 1 || matrix[1].Count != 0 || matrix[2].Count != 1)
                    throw new InvalidOperationException("Nested binary list shape changed.");
                AssertPayload(matrix[0][0], id);
                AssertPayload(matrix[2][0], id);

                var memories = row.Memories;
                if (memories.Length != 2 || !memories[1].IsEmpty)
                    throw new InvalidOperationException("Binary memory array shape changed.");
                AssertMemoryPayload(memories[0], id);

                var memoryList = row.MemoryList;
                if (memoryList.Count != 2 || !memoryList[1].IsEmpty)
                    throw new InvalidOperationException("Binary memory list shape changed.");
                AssertMemoryPayload(memoryList[0], id);
                AssertOptionalMemories(row.OptionalMemories, id);
                AssertOptionalMemories(row.OptionalMemoryList, id);

                var memoryMap = row.MemoryMap;
                if (memoryMap.Count != 3 || memoryMap[1] is not { } mappedMemory ||
                    memoryMap[2].HasValue || memoryMap[3] is not { IsEmpty: true })
                    throw new InvalidOperationException("Nullable binary memory map values changed.");
                AssertMemoryPayload(mappedMemory, id);
            }
        }
        if (seen.Count != 12)
            throw new InvalidOperationException($"Expected 12 rows, read {seen.Count}.");
    }

    static void AssertPayload(byte[]? value, int id)
    {
        if (value is not { Length: 4 } || value.AsSpan().ContainsAnyExcept(checked((byte)id)))
            throw new InvalidOperationException($"Nested binary data changed for row {id}.");
    }

    static void AssertMemoryPayload(ReadOnlyMemory<byte> value, int id)
    {
        if (value.Length != 2 || value.Span.ContainsAnyExcept(checked((byte)id)))
            throw new InvalidOperationException($"Nested binary memory slice changed for row {id}.");
    }

    static void AssertOptionalMemories(IReadOnlyList<ReadOnlyMemory<byte>?>? values, int id)
    {
        if (id % 3 == 0)
        {
            if (values is not null)
                throw new InvalidOperationException("A null memory collection became non-null.");
        }
        else if (id % 3 == 1)
        {
            if (values is not { Count: 0 })
                throw new InvalidOperationException("An empty memory collection changed.");
        }
        else
        {
            if (values is not { Count: 3 } || values[0] is not { } memory ||
                values[1].HasValue || values[2] is not { IsEmpty: true })
                throw new InvalidOperationException("Nullable binary memory collection elements changed.");
            AssertMemoryPayload(memory, id);
        }
    }

    static ReadOnlySpan<byte> Route(DatasetNestedBinarySnapshotRow row, IParquetBufferPool bufferPool,
        out ParquetBuffer? allocation)
    {
        allocation = null;
        return (row.Id % 3) switch { 0 => "a.parquet"u8, 1 => "b.parquet"u8, _ => "c.parquet"u8 };
    }

    sealed class MemoryDatasetFile : IParquetReadSource, IParquetWriteSource
    {
        readonly Dictionary<string, MemoryStream> _files = [];
        MemoryStream? _current;

        public ulong Length => checked((ulong)Current.Length);

        MemoryStream Current => _current ?? throw new InvalidOperationException("No file is open.");

        internal IEnumerable<byte[]> GetFiles() => _files.Values.Select(static stream => stream.ToArray());

        public void Open(ReadOnlySpan<byte> path, FileMode mode)
        {
            if (_current is not null)
                throw new InvalidOperationException("A file is already open.");
            var name = Encoding.UTF8.GetString(path);
            if (!_files.TryGetValue(name, out _current))
                _files.Add(name, _current = new MemoryStream());
            if (mode is FileMode.Create or FileMode.CreateNew)
                _current.SetLength(0);
        }

        public void Close() => _current = null;

        public void Flush() { }

        public void ReadExactly(ulong offset, Span<byte> destination)
        {
            Current.Position = checked((long)offset);
            Current.ReadExactly(destination);
        }

        public void Write(ulong offset, ReadOnlySpan<byte> source)
        {
            Current.Position = checked((long)offset);
            Current.Write(source);
        }

        public void SetLength(ulong length) => Current.SetLength(checked((long)length));

        public void Dispose()
        {
            foreach (var stream in _files.Values)
                stream.Dispose();
            _current = null;
        }
    }
}

[ParquetSchema(AllowAllocatingValues = true)]
internal sealed partial class DatasetNestedBinarySnapshotRow
{
    public int Id { get; set; }

    public DatasetNestedBinaryDetails? Details { get; set; }

    public List<byte[]?>? Items { get; set; }

    public Dictionary<int, byte[]?> Map { get; set; } = [];

    public List<List<byte[]>> Matrix { get; set; } = [];

    public ReadOnlyMemory<byte>[] Memories { get; set; } = [];

    public ReadOnlyMemory<byte>?[]? OptionalMemories { get; set; }

    public List<ReadOnlyMemory<byte>> MemoryList { get; set; } = [];

    public List<ReadOnlyMemory<byte>?>? OptionalMemoryList { get; set; }

    public Dictionary<int, ReadOnlyMemory<byte>?> MemoryMap { get; set; } = [];
}

internal sealed class DatasetNestedBinaryDetails
{
    public int Id { get; set; }

    public byte[] Payload { get; set; } = [];
}
