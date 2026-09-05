using System.Text;
using System.Runtime.InteropServices;
using Plank.Dataset;
using Plank.Reading;
using Plank.Reading.Physical;
using Plank.Writing;

namespace Plank.Tests.E2E;

internal sealed class DatasetWriterTests
{
    [Test]
    public async Task GeneratedDatasetWriterDoesNotExposeBufferSlot()
    {
        var baseType = typeof(DatasetRowSchema.DatasetWriter).BaseType!;

        await Assert.That(baseType.GetGenericTypeDefinition()).IsEqualTo(typeof(DatasetWriterBase<>));
        await Assert.That(baseType.GetGenericArguments().Length).IsEqualTo(1);
    }

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
                PendingRowCapacity = 2
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
                PendingRowCapacity = 0
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
                    PendingRowCapacity = 256
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
    [Arguments(false, 1024)]
    [Arguments(true, 1024)]
    [Arguments(false, 70000)]
    [Arguments(true, 70000)]
    public async Task BinaryQueueDoesNotAllocateAcrossSnapshotChunks(bool parked, int payloadSize)
    {
        var activePath = NewPath();
        var parkedPath = NewPath();
        try
        {
            using var pool = new DefaultParquetBufferPool();
            var options = new ParquetWriterOptions { BufferPool = pool, RowApiInitialRowCapacity = 128 };
            using var writer = DatasetBinaryRowSchema.CreateDatasetWriter(SelectBinaryPath, CreateFiles(1),
                new DatasetWriterOptions
                {
                    PendingRowCapacity = 128,
                    WriterOptions = options,
                    AppendOptions = new ParquetAppendOptions { WriterOptions = options }
                });
            var bytes = new byte[payloadSize];
            var row = new DatasetBinaryRowSchema
            {
                Path = Encoding.UTF8.GetBytes(parked ? parkedPath : activePath),
                Payload = bytes,
                OptionalPayload = bytes,
                Memory = bytes.AsMemory(1, payloadSize - 2),
                OptionalMemory = bytes.AsMemory(2, payloadSize - 4)
            };
            writer.Queue(new DatasetBinaryRowSchema { Path = Encoding.UTF8.GetBytes(activePath) });
            for (var i = 0; i < 8; i++)
                writer.Queue(row);

            // Stay within row capacity and below the flush threshold, but consume many binary chunks.
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 100; i++)
                writer.Queue(row);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
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
                PendingRowCapacity = 1
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
                PendingRowCapacity = 1
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
                    PendingRowCapacity = 4
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

    [Test]
    public async Task RollsPartitionRowsIntoTargetSizedFiles()
    {
        using var paths = new DatasetPartPaths();
        using (var writer = DatasetRowSchema.CreateDatasetWriter(SelectPath, paths.SelectPath, CreateFiles(1),
                   new DatasetWriterOptions
                   {
                       PendingRowCapacity = 0,
                       WriterOptions = new ParquetWriterOptions
                       {
                           TargetRowGroupSizeBytes = 20,
                           TargetFileSizeBytes = 1
                       }
                   }))
        {
            for (var i = 0; i < 10; i++)
                writer.Queue(new DatasetRowSchema { Value = i, Path = "a"u8.ToArray() });
        }

        await Assert.That(paths.Paths.Count).IsEqualTo(3);
        var values = new List<int>();
        for (var i = 0; i < paths.Paths.Count; i++)
            values.AddRange(ReadValues(paths.Paths[i]));
        await Assert.That(values.SequenceEqual(Enumerable.Range(0, 10))).IsTrue();
    }

    [Test]
    [Arguments(0, 16)]
    [Arguments(3, 16)]
    [Arguments(3, 1000)]
    public async Task QueueSnapshotsBinaryValues(int pendingCapacity, int payloadSize)
    {
        var paths = Enumerable.Range(0, 3).Select(_ => NewPath()).ToArray();
        var pathBytes = paths.Select(Encoding.UTF8.GetBytes).ToArray();
        var pool = new OwnershipTrackingPool();
        try
        {
            using (var writer = DatasetBinaryRowSchema.CreateDatasetWriter(SelectBinaryPath, CreateFiles(1),
                       BinaryOptions(pool, pendingCapacity)))
            {
                var reused = new byte[payloadSize];
                for (var id = 0; id < 80; id++)
                {
                    Array.Fill(reused, checked((byte)id));
                    writer.Queue(new DatasetBinaryRowSchema
                    {
                        Path = pathBytes[pendingCapacity == 0 ? 0 : id % 3],
                        Id = id,
                        Payload = reused,
                        OptionalPayload = id % 3 == 0 ? null : id % 3 == 1 ? [] : reused,
                        Memory = reused.AsMemory(2, payloadSize - 4),
                        OptionalMemory = id % 3 == 0 ? (ReadOnlyMemory<byte>?)null : id % 3 == 1
                            ? ReadOnlyMemory<byte>.Empty : reused.AsMemory(3, payloadSize - 6)
                    });
                    Array.Fill(reused, (byte)255);
                }
            }

            await Assert.That(pool.Outstanding).IsEqualTo(0);
            var seen = new HashSet<int>();
            foreach (var path in paths.Where(File.Exists))
            {
                using var stream = File.OpenRead(path);
                using var reader = DatasetBinaryRowSchema.CreateRowReader(stream);
                while (reader.MoveNext())
                {
                    var row = reader.Current;
                    if (!seen.Add(row.Id))
                        throw new InvalidOperationException("A queued row was duplicated.");
                    AssertPayload(row.Payload.Value, row.Id, payloadSize);
                    AssertPayload(row.Memory.Value, row.Id, payloadSize - 4);
                    var optionalPayload = row.OptionalPayload;
                    var optionalMemory = row.OptionalMemory;
                    if (optionalPayload.IsNull != (row.Id % 3 == 0) || optionalMemory.IsNull != (row.Id % 3 == 0))
                        throw new InvalidOperationException("Queue changed binary nullability.");
                    if (!optionalPayload.IsNull)
                    {
                        AssertPayload(optionalPayload.Value, row.Id, row.Id % 3 == 1 ? 0 : payloadSize);
                        AssertPayload(optionalMemory.Value, row.Id, row.Id % 3 == 1 ? 0 : payloadSize - 6);
                    }
                }
            }
            await Assert.That(seen.Count).IsEqualTo(80);
        }
        finally
        {
            foreach (var path in paths)
                DeleteIfPresent(path);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task FailedQueueReleasesBinarySnapshotsAndReusesTheRow(bool parked)
    {
        var activePath = NewPath();
        var failedPath = parked ? NewPath() : activePath;
        var pool = new OwnershipTrackingPool();
        try
        {
            using (var writer = DatasetBinaryRowSchema.CreateDatasetWriter(SelectBinaryPath, CreateFiles(1),
                       BinaryOptions(pool, 3)))
            {
                writer.Queue(new DatasetBinaryRowSchema { Path = Encoding.UTF8.GetBytes(activePath), Id = 1 });
                var badRow = new DatasetBinaryRowSchema
                {
                    Path = Encoding.UTF8.GetBytes(failedPath),
                    Id = 2,
                    Payload = new byte[70000],
                    Memory = new byte[70000],
                    ThrowOnTail = true
                };
                await Assert.That(() => writer.Queue(badRow)).Throws<InvalidOperationException>();
                writer.Queue(new DatasetBinaryRowSchema { Path = badRow.Path, Id = 3 });
            }
            await Assert.That(pool.Outstanding).IsEqualTo(0);
            using var stream = File.OpenRead(failedPath);
            using var reader = DatasetBinaryRowSchema.CreateRowReader(stream);
            var ids = new List<int>();
            while (reader.MoveNext())
            {
                ids.Add(reader.Current.Id);
                if (!reader.Current.Payload.Value.IsEmpty || !reader.Current.Memory.Value.IsEmpty)
                    throw new InvalidOperationException("A failed row left binary data in the reused slot.");
            }
            await Assert.That(ids).IsEquivalentTo(parked ? new List<int> { 3 } : [1, 3]);
        }
        finally
        {
            DeleteIfPresent(activePath);
            DeleteIfPresent(failedPath);
        }
    }

    [Test]
    public async Task DiscardedParkedRowsReleaseBinarySnapshotsWhenOpeningFails()
    {
        var activePath = NewPath();
        var parkedPath = NewPath();
        var pool = new OwnershipTrackingPool();
        var files = CreateFiles(1);
        try
        {
            var writer = DatasetBinaryRowSchema.CreateDatasetWriter(SelectBinaryPath, files, BinaryOptions(pool, 3));
            writer.Queue(new DatasetBinaryRowSchema { Path = Encoding.UTF8.GetBytes(activePath) });
            writer.Queue(new DatasetBinaryRowSchema
            {
                Path = Encoding.UTF8.GetBytes(parkedPath),
                Payload = new byte[70000],
                Memory = new byte[70000]
            });
            files[0].FailOpen = true;
            await Assert.That(() => writer.Dispose()).Throws<IOException>();
            await Assert.That(pool.Outstanding).IsEqualTo(0);
        }
        finally
        {
            files[0].Dispose();
            DeleteIfPresent(activePath);
            DeleteIfPresent(parkedPath);
        }
    }

    static void AssertPayload(ReadOnlySpan<byte> actual, int id, int length)
    {
        if (actual.Length != length || actual.ContainsAnyExcept(checked((byte)id)))
            throw new InvalidOperationException($"Queued binary value changed for row {id} (expected {length} bytes).");
    }

    static DatasetWriterOptions BinaryOptions(IParquetBufferPool pool, int pendingCapacity)
    {
        var writerOptions = new ParquetWriterOptions
        {
            BufferPool = pool,
            RowApiInitialRowCapacity = 2
        };
        return new DatasetWriterOptions
        {
            PendingRowCapacity = checked((uint)pendingCapacity),
            WriterOptions = writerOptions,
            AppendOptions = new ParquetAppendOptions { WriterOptions = writerOptions }
        };
    }

    static ReadOnlySpan<byte> SelectBinaryPath(DatasetBinaryRowSchema row, IParquetBufferPool pool,
        out ParquetBuffer? allocation)
    {
        _ = pool;
        allocation = null;
        return row.Path;
    }

    sealed class OwnershipTrackingPool : IParquetBufferPool
    {
        internal int Outstanding;

        public ParquetBuffer Rent(uint minimumByteLength)
        {
            if (minimumByteLength == 0)
                return default;
            var length = checked((int)minimumByteLength);
            var allocation = Marshal.AllocHGlobal(checked(length + 64));
            Outstanding++;
            return ParquetBuffer.Create(allocation, length + 64, 64, length, Return);
        }

        void Return(nint allocation)
        {
            Outstanding--;
            Marshal.FreeHGlobal(allocation);
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
        internal bool FailOpen;
        internal readonly List<string> OpenedPaths = [];

        public ulong Length
            => checked((ulong)GetStream().Length);

        public void Open(ReadOnlySpan<byte> path, FileMode mode)
        {
            if (FailOpen)
                throw new IOException("Test open failure.");
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

    sealed class DatasetPartPaths : IDisposable
    {
        readonly List<byte[]> _pathsUtf8 = [];

        internal readonly List<string> Paths = [];

        internal ReadOnlySpan<byte> SelectPath(ReadOnlySpan<byte> partitionKey, ulong fileIndex,
            IParquetBufferPool bufferPool, out ParquetBuffer? allocation)
        {
            _ = partitionKey;
            _ = bufferPool;
            allocation = null;
            if (fileIndex != checked((ulong)_pathsUtf8.Count))
                throw new InvalidOperationException("The dataset file index is not sequential.");
            var path = NewPath();
            Paths.Add(path);
            var pathUtf8 = Encoding.UTF8.GetBytes(path);
            _pathsUtf8.Add(pathUtf8);
            return pathUtf8;
        }

        public void Dispose()
        {
            for (var i = 0; i < Paths.Count; i++)
                DeleteIfPresent(Paths[i]);
        }
    }
}
