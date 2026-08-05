using System.Text;
using Plank.Dataset;
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
            using (var writer = DatasetRowSchema.CreateDatasetWriter(SelectPath, new DatasetWriterOptions
            {
                MaximumActiveWriters = 1,
                MaximumPendingPartitions = 2,
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
            using (var writer = DatasetRowSchema.CreateDatasetWriter(SelectPath, new DatasetWriterOptions
            {
                MaximumActiveWriters = 1,
                MaximumPendingPartitions = 0,
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
            var writer = DatasetRowSchema.CreateDatasetWriter(SelectPath, new DatasetWriterOptions
            {
                MaximumActiveWriters = 1,
                MaximumPendingPartitions = 0,
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
    public async Task SecondDisposeThrows()
    {
        var writer = DatasetRowSchema.CreateDatasetWriter(SelectPath);
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
            using (var writer = DatasetRowSchema.CreateDatasetWriter(SelectAllocatedPath, new DatasetWriterOptions
            {
                MaximumActiveWriters = 1,
                MaximumPendingPartitions = 1,
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
}
