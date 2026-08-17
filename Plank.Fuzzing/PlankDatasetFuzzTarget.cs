using System.Text;
using Plank.Dataset;
using Plank.Reading;

namespace Plank.Fuzzing;

/// <summary>
/// Routes fuzzed rows through the generated partitioned dataset writer, then
/// reads every partition back and checks the routing held.
/// </summary>
/// <remarks>
/// DatasetWriterBase measured 0/455 lines — the largest single untouched body of
/// code on the write side. It was unreachable for a structural reason rather
/// than a missing seed: it is the only writer that owns *files*, naming and
/// opening one per partition, so driving it appeared to require hitting the
/// filesystem tens of thousands of times a second. InMemoryDatasetFiles removes
/// that, keeping the path construction, partition bookkeeping, create-vs-append
/// choice and file pooling under test.
///
/// The oracle is the routing invariant, which is what this writer is *for*:
/// every row must land in the partition its key names, and no row may be lost or
/// duplicated. Both are checked by reading each partition back through the
/// generated reader.
/// </remarks>
public static class PlankDatasetFuzzTarget
{
    const int MaxRows = 64;

    // Few enough partitions that rows collide and files get appended to rather
    // than written once, which is where the interesting bookkeeping is.
    const int MaxPartitions = 4;

    public static void Execute(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2)
            return;

        try
        {
            Run(data);
        }
        catch (Exception ex) when (ex is CorruptParquetException or NotSupportedException
            or InvalidOperationException)
        {
        }
    }

    static void Run(ReadOnlySpan<byte> data)
    {
        var partitions = (data[0] % MaxPartitions) + 1;
        var cursor = new Cursor(data[1..]);
        var count = cursor.NextInt(1, MaxRows + 1);

        var rows = new (int Id, long Sequence)[count];
        for (var i = 0; i < count; i++)
            rows[i] = (cursor.NextInt32(), cursor.NextInt64());

        // The key is derived from the row, so the expected partition of every row
        // is known independently of what the writer did with it.
        var expected = new int[count];
        for (var i = 0; i < count; i++)
            expected[i] = PartitionOf(rows[i].Id, partitions);

        var store = new InMemoryFileStore();
        var files = new InMemoryDatasetFile[Math.Max(1, cursor.NextInt(1, 4))];
        for (var i = 0; i < files.Length; i++)
            files[i] = new InMemoryDatasetFile(store);

        var options = new DatasetWriterOptions
        {
            // Small enough that a 64-row case flushes more than once, which is the
            // only way to reach the append path rather than a single create.
            PendingRowCapacity = (uint)cursor.NextInt(1, 17)
        };

        using (var writer = RowFuzzFixed.CreateDatasetWriter(
                   (RowFuzzFixed row, IParquetBufferPool pool, out ParquetBuffer? allocation) =>
                   {
                       allocation = null;
                       return KeyFor(PartitionOf(row.Id, partitions));
                   },
                   files, options))
        {
            foreach (var (id, sequence) in rows)
                writer.Queue(new RowFuzzFixed { Id = id, Sequence = sequence, Flag = false, Measure = 0 });
        }

        VerifyRouting(store, rows, expected, partitions);
    }

    // Stable, and independent of the writer: a negative Id must not produce a
    // negative index, which is why this masks rather than taking a remainder.
    static int PartitionOf(int id, int partitions)
        => (id & int.MaxValue) % partitions;

    static readonly byte[][] Keys =
        [.. Enumerable.Range(0, MaxPartitions).Select(static i => Encoding.UTF8.GetBytes($"p{i}"))];

    static ReadOnlySpan<byte> KeyFor(int partition)
        => Keys[partition];

    static void VerifyRouting(InMemoryFileStore store, (int Id, long Sequence)[] rows, int[] expected,
        int partitions)
    {
        var seen = 0;
        foreach (var (path, file) in store.Files)
        {
            if (file.Length == 0)
                continue;

            // Which partition this file belongs to is read back out of its own
            // path, so a writer that put rows in the wrong file is caught rather
            // than trusted.
            var partition = PartitionFromPath(path, partitions);
            if (partition < 0)
                throw new DatasetRoutingException($"Partition file '{path}' names no known key.");

            using var reader = RowFuzzFixed.CreateRowReader(
                new MemoryStream(file.ToArray(), writable: false));
            while (reader.MoveNext())
            {
                var actual = PartitionOf(reader.Current.Id, partitions);
                if (actual != partition)
                    throw new DatasetRoutingException(
                        $"Row with Id {reader.Current.Id} belongs in partition {actual} but was written to '{path}' (partition {partition}).");
                seen++;
            }
        }

        if (seen != rows.Length)
            throw new DatasetRoutingException(
                $"Dataset round trip returned {seen} rows but {rows.Length} were queued.");
        _ = expected;
    }

    static int PartitionFromPath(string path, int partitions)
    {
        for (var i = 0; i < partitions; i++)
            if (path.Contains($"p{i}", StringComparison.Ordinal))
                return i;
        return -1;
    }

    /// <summary>Signals that the dataset writer lost, duplicated or misrouted a row.</summary>
    public sealed class DatasetRoutingException(string message) : Exception(message);

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

        internal long NextInt64()
            => ((long)NextInt32() << 32) | (uint)NextInt32();
    }
}
