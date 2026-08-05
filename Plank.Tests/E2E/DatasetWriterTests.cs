using System.Text;
using Plank.Dataset;
using Plank.Reading;
using Plank.Reading.Physical;
using Plank.Writing;

namespace Plank.Tests.E2E;

internal sealed class DatasetWriterTests
{
    [Test]
    public async Task RoutesRowsAndReopensForgottenPartitions()
    {
        var pathA = NewPath();
        var pathB = NewPath();
        try
        {
            var pathAUtf8 = Encoding.UTF8.GetBytes(pathA);
            var pathBUtf8 = Encoding.UTF8.GetBytes(pathB);
            var files = CreateFiles(1);
            using (var writer = DatasetRowSchema.CreateDatasetWriter(SelectPath, files, new DatasetWriterOptions
            {
                PendingRowCapacity = 2,
                RowsBeforeWriterActivation = 2
            }))
            {
                writer.Queue(new DatasetRowSchema { Value = 1, Path = pathAUtf8 });
                writer.Queue(new DatasetRowSchema { Value = 2, Path = pathBUtf8 });
                writer.Queue(new DatasetRowSchema { Value = 3, Path = pathAUtf8 });
                writer.Queue(new DatasetRowSchema { Value = 4, Path = pathBUtf8 });
                writer.Queue(new DatasetRowSchema { Value = 5, Path = pathAUtf8 });
                writer.Queue(new DatasetRowSchema { Value = 6, Path = pathBUtf8 });
            }

            await Assert.That(files[0].OpenCount).IsGreaterThan(1);
            await Assert.That(ReadValues(pathA)).IsEquivalentTo([1, 3, 5]);
            await Assert.That(ReadValues(pathB)).IsEquivalentTo([2, 4, 6]);
            await Assert.That(ReadRowGroupCount(pathA)).IsEqualTo(2);
        }
        finally
        {
            DeleteIfPresent(pathA);
            DeleteIfPresent(pathB);
        }
    }

    [Test]
    public async Task AppendToLatestRowGroupRewritesTheLastGroup()
    {
        var pathA = NewPath();
        var pathB = NewPath();
        try
        {
            var pathAUtf8 = Encoding.UTF8.GetBytes(pathA);
            var pathBUtf8 = Encoding.UTF8.GetBytes(pathB);
            using (var writer = DatasetRowSchema.CreateDatasetWriter(SelectPath, CreateFiles(1),
                new DatasetWriterOptions
            {
                PendingRowCapacity = 0,
                RowsBeforeWriterActivation = 1,
                AppendOptions = new ParquetAppendOptions
                {
                    AppendToLatestRowGroup = true
                }
            }))
            {
                writer.Queue(new DatasetRowSchema { Value = 1, Path = pathAUtf8 });
                writer.Queue(new DatasetRowSchema { Value = 2, Path = pathBUtf8 });
                writer.Queue(new DatasetRowSchema { Value = 3, Path = pathAUtf8 });
                writer.Queue(new DatasetRowSchema { Value = 4, Path = pathBUtf8 });
            }

            await Assert.That(ReadValues(pathA)).IsEquivalentTo([1, 3]);
            await Assert.That(ReadValues(pathB)).IsEquivalentTo([2, 4]);
            await Assert.That(ReadRowGroupCount(pathA)).IsEqualTo(1);
            await Assert.That(ReadRowGroupCount(pathB)).IsEqualTo(1);
        }
        finally
        {
            DeleteIfPresent(pathA);
            DeleteIfPresent(pathB);
        }
    }

    [Test]
    public async Task ActivePartitionQueueDoesNotAllocate()
    {
        var path = NewPath();
        try
        {
            var pathUtf8 = Encoding.UTF8.GetBytes(path);
            var row = new DatasetRowSchema { Path = pathUtf8 };
            var writer = DatasetRowSchema.CreateDatasetWriter(SelectPath, CreateFiles(1),
                new DatasetWriterOptions
            {
                PendingRowCapacity = 0,
                RowsBeforeWriterActivation = 1
            });
            writer.Queue(row);
            for (var i = 0; i < 8; i++)
                writer.Queue(row);

            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 100; i++)
                writer.Queue(row);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            writer.Dispose();

            await Assert.That(allocated).IsEqualTo(0);
        }
        finally
        {
            DeleteIfPresent(path);
        }
    }

    [Test]
    public async Task ParkedPartitionQueueDoesNotAllocate()
    {
        var activePath = NewPath();
        var parkedPath = NewPath();
        try
        {
            var activePathUtf8 = Encoding.UTF8.GetBytes(activePath);
            var parkedRow = new DatasetRowSchema { Path = Encoding.UTF8.GetBytes(parkedPath) };
            var writer = DatasetRowSchema.CreateDatasetWriter(SelectPath, CreateFiles(1),
                new DatasetWriterOptions
                {
                    PendingRowCapacity = 256,
                    RowsBeforeWriterActivation = 256
                });
            writer.Queue(new DatasetRowSchema { Path = activePathUtf8 });
            for (var i = 0; i < 8; i++)
                writer.Queue(parkedRow);

            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 100; i++)
                writer.Queue(parkedRow);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            writer.Dispose();

            await Assert.That(allocated).IsEqualTo(0);
        }
        finally
        {
            DeleteIfPresent(activePath);
            DeleteIfPresent(parkedPath);
        }
    }

    [Test]
    public async Task SecondDisposeThrows()
    {
        var writer = DatasetRowSchema.CreateDatasetWriter(SelectPath, CreateFiles(1));
        writer.Dispose();

        await Assert.That(() => writer.Dispose()).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task KeepsTheReturnedPathAllocationUntilThePartitionCloses()
    {
        var path = NewPath();
        try
        {
            var pathUtf8 = Encoding.UTF8.GetBytes(path);
            using (var writer = DatasetRowSchema.CreateDatasetWriter(SelectAllocatedPath,
                CreateFiles(1), new DatasetWriterOptions
            {
                PendingRowCapacity = 1,
                RowsBeforeWriterActivation = 1
            }))
                writer.Queue(new DatasetRowSchema { Value = 42, Path = pathUtf8 });

            await Assert.That(ReadValues(path)).IsEquivalentTo([42]);
        }
        finally
        {
            DeleteIfPresent(path);
        }
    }

    [Test]
    public async Task ReturnsTheLeastBusyWriterForAParkedPartition()
    {
        var pathA = NewPath();
        var pathB = NewPath();
        var pathC = NewPath();
        try
        {
            var pathAUtf8 = Encoding.UTF8.GetBytes(pathA);
            var pathBUtf8 = Encoding.UTF8.GetBytes(pathB);
            var pathCUtf8 = Encoding.UTF8.GetBytes(pathC);
            var files = CreateFiles(2);
            using (var writer = DatasetRowSchema.CreateDatasetWriter(SelectPath, files, new DatasetWriterOptions
            {
                PendingRowCapacity = 4,
                RowsBeforeWriterActivation = 1
            }))
            {
                for (var value = 1; value <= 5; value++)
                    writer.Queue(new DatasetRowSchema { Value = value, Path = pathAUtf8 });
                writer.Queue(new DatasetRowSchema { Value = 10, Path = pathBUtf8 });
                writer.Queue(new DatasetRowSchema { Value = 20, Path = pathCUtf8 });
                writer.Queue(new DatasetRowSchema { Value = 6, Path = pathAUtf8 });
            }

            var pathAOpenCount = files.Sum(file => file.OpenedPaths.Count(path => path == pathA));
            await Assert.That(pathAOpenCount).IsEqualTo(1);
            await Assert.That(ReadValues(pathA)).IsEquivalentTo([1, 2, 3, 4, 5, 6]);
            await Assert.That(ReadValues(pathB)).IsEquivalentTo([10]);
            await Assert.That(ReadValues(pathC)).IsEquivalentTo([20]);
        }
        finally
        {
            DeleteIfPresent(pathA);
            DeleteIfPresent(pathB);
            DeleteIfPresent(pathC);
        }
    }

    [Test]
    public async Task ReusesParkedRowsWithoutCompactingOtherPartitions()
    {
        var pathA = NewPath();
        var pathB = NewPath();
        var pathC = NewPath();
        var pathD = NewPath();
        try
        {
            var pathAUtf8 = Encoding.UTF8.GetBytes(pathA);
            var pathBUtf8 = Encoding.UTF8.GetBytes(pathB);
            var pathCUtf8 = Encoding.UTF8.GetBytes(pathC);
            var pathDUtf8 = Encoding.UTF8.GetBytes(pathD);
            using (var writer = DatasetRowSchema.CreateDatasetWriter(SelectPath, CreateFiles(1),
                new DatasetWriterOptions
                {
                    PendingRowCapacity = 4,
                    RowsBeforeWriterActivation = 4
                }))
            {
                writer.Queue(new DatasetRowSchema { Value = 1, Path = pathAUtf8 });
                writer.Queue(new DatasetRowSchema { Value = 10, Path = pathBUtf8 });
                writer.Queue(new DatasetRowSchema { Value = 20, Path = pathCUtf8 });
                writer.Queue(new DatasetRowSchema { Value = 11, Path = pathBUtf8 });
                writer.Queue(new DatasetRowSchema { Value = 21, Path = pathCUtf8 });
                writer.Queue(new DatasetRowSchema { Value = 12, Path = pathBUtf8 });
                writer.Queue(new DatasetRowSchema { Value = 30, Path = pathDUtf8 });
                writer.Queue(new DatasetRowSchema { Value = 13, Path = pathBUtf8 });
            }

            await Assert.That(ReadValues(pathB).SequenceEqual([10, 11, 12, 13])).IsTrue();
            await Assert.That(ReadValues(pathC).SequenceEqual([20, 21])).IsTrue();
            await Assert.That(ReadValues(pathD)).IsEquivalentTo([30]);
        }
        finally
        {
            DeleteIfPresent(pathA);
            DeleteIfPresent(pathB);
            DeleteIfPresent(pathC);
            DeleteIfPresent(pathD);
        }
    }

    static ReadOnlySpan<byte> SelectPath(DatasetRowSchema row, IParquetBufferPool bufferPool,
        out ParquetBuffer? allocation)
    {
        _ = bufferPool;
        allocation = null;
        return row.Path.Span;
    }

    static ReadOnlySpan<byte> SelectAllocatedPath(DatasetRowSchema row, IParquetBufferPool bufferPool,
        out ParquetBuffer? allocation)
    {
        var owner = bufferPool.Rent(checked((uint)row.Path.Length + 4));
        row.Path.Span.CopyTo(owner.Span[2..]);
        allocation = owner;
        return owner.Span.Slice(2, row.Path.Length);
    }

    static List<int> ReadValues(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = DatasetRowSchema.CreateRowReader(stream);
        var result = new List<int>();
        while (reader.MoveNext())
            result.Add(reader.Current.Value);
        return result;
    }

    static int ReadRowGroupCount(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new ParquetFileReader();
        reader.Reset(stream);
        return reader.Metadata.RowGroupCount;
    }

    static string NewPath()
        => Path.Combine(Path.GetTempPath(), $"plank-dataset-{Guid.NewGuid():N}.parquet");

    static void DeleteIfPresent(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    static TestParquetSource[] CreateFiles(int count)
    {
        var result = new TestParquetSource[count];
        for (var i = 0; i < result.Length; i++)
            result[i] = new TestParquetSource();
        return result;
    }

    sealed class TestParquetSource : IParquetReadSource, IParquetWriteSource, IDisposable
    {
        FileStream? _stream;
        internal int OpenCount;
        internal readonly List<string> OpenedPaths = [];

        public ulong Length
            => checked((ulong)GetStream().Length);

        public void Open(ReadOnlySpan<byte> path, FileMode mode)
        {
            if (_stream is not null)
                throw new InvalidOperationException("The file is already open.");
            var filePath = Encoding.UTF8.GetString(path);
            OpenedPaths.Add(filePath);
            _stream = new FileStream(filePath, mode, FileAccess.ReadWrite, FileShare.None);
            OpenCount++;
        }

        public void Close()
        {
            _stream?.Dispose();
            _stream = null;
        }

        public void ReadExactly(ulong offset, Span<byte> destination)
        {
            var stream = GetStream();
            stream.Position = checked((long)offset);
            stream.ReadExactly(destination);
        }

        public void Write(ulong offset, ReadOnlySpan<byte> source)
        {
            var stream = GetStream();
            stream.Position = checked((long)offset);
            stream.Write(source);
        }

        public void SetLength(ulong length)
            => GetStream().SetLength(checked((long)length));

        public void Flush()
            => GetStream().Flush();

        public void Dispose()
            => Close();

        FileStream GetStream()
            => _stream ?? throw new InvalidOperationException("The file is not open.");
    }
}
