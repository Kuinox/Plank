using System.Text;
using Plank.Reading;
using Plank.Reading.Physical;
using Plank.RowApi;
using Plank.Writing;

namespace Plank.Tests.E2E;

internal sealed class RowWriterSizeTests
{
    [Test]
    public async Task RowValueSizesAreClassifiedAtInitialization()
    {
        var valueColumn = DatasetRowSchema.Schema.LeafColumns[0].Column;
        var pathColumn = DatasetRowSchema.Schema.LeafColumns[1].Column;
        var fixedBinaryColumn = AdditionalLogicalAnnotationRowSchema.Schema.LeafColumns[2].Column;

        await Assert.That(RowValueSizeEstimator.TryGetFixedSize<int>(valueColumn, out var valueSize)).IsTrue();
        await Assert.That(valueSize).IsEqualTo(4UL);
        await Assert.That(RowValueSizeEstimator.TryGetFixedSize<ReadOnlyMemory<byte>>(pathColumn, out _)).IsFalse();
        await Assert.That(RowValueSizeEstimator.TryGetFixedSize<byte[]>(fixedBinaryColumn, out var byteArraySize)).IsTrue();
        await Assert.That(byteArraySize).IsEqualTo(2UL);
        await Assert.That(RowValueSizeEstimator.TryGetFixedSize<ReadOnlyMemory<byte>>(fixedBinaryColumn,
            out var memorySize)).IsTrue();
        await Assert.That(memorySize).IsEqualTo(2UL);
        await Assert.That(RowValueSizeEstimator.TryGetFixedSize<ReadOnlyMemory<byte>?>(fixedBinaryColumn,
            out var optionalMemorySize)).IsTrue();
        await Assert.That(optionalMemorySize).IsEqualTo(2UL);
        await Assert.That(RowValueSizeEstimator.TryGetFixedSize<byte[][]>(fixedBinaryColumn, out _)).IsFalse();
    }

    [Test]
    public void GeneratedSchemaWriterDisposalAbortsAndRejectsReuse()
    {
        using var stream = new MemoryStream();
        using var resetStream = new MemoryStream();
        var writer = DatasetRowSchema.CreateWriter(stream);

        writer.Dispose();
        writer.Dispose();

        Assert.Throws<ObjectDisposedException>(() => writer.StartRowGroup());
        Assert.Throws<ObjectDisposedException>(() => writer.Reset(resetStream));
        Assert.Throws<ObjectDisposedException>(() => writer.CloseFile());
        if (!stream.ToArray().AsSpan().SequenceEqual("PAR1"u8))
            throw new InvalidOperationException("Disposing an incomplete schema writer wrote a Parquet footer.");
    }

    [Test]
    public void GeneratedPipelineWriterDisposalAbortsAndRejectsReuse()
    {
        using var stream = new MemoryStream();
        var writer = DatasetRowSchema.CreateRowWriter(stream, maxParallelism: 1,
            new ParquetWriterOptions());
        var row = writer.GetRow();
        row.Value = 42;
        row.Path = ReadOnlyMemory<byte>.Empty;

        writer.Dispose();
        writer.Dispose();

        Assert.Throws<ObjectDisposedException>(() => writer.GetRow());
        Assert.Throws<ObjectDisposedException>(() => writer.Complete());
        if (!stream.ToArray().AsSpan().SequenceEqual("PAR1"u8))
            throw new InvalidOperationException("Disposing an incomplete pipeline writer wrote a Parquet footer.");
    }

    [Test]
    public async Task GeneratedRowWriterTargetsRowGroupsByBufferedSize()
    {
        var path = NewPath();
        try
        {
            using (var writeStream = File.Create(path))
            {
                using var writer = DatasetRowSchema.CreateRowWriter(writeStream, new ParquetWriterOptions
                {
                    RowApiMaxParallelism = 1,
                    TargetRowGroupSizeBytes = 16
                });
                WriteRows(writer, 10);
                writer.Complete();
            }

            using var readStream = File.OpenRead(path);
            using var reader = new ParquetFileReader();
            reader.Reset(readStream);
            await Assert.That(reader.Metadata.RowGroupCount).IsEqualTo(3);
            await Assert.That(reader.Metadata.RowGroup(0).RowCount).IsEqualTo(4UL);
            await Assert.That(reader.Metadata.RowGroup(1).RowCount).IsEqualTo(4UL);
            await Assert.That(reader.Metadata.RowGroup(2).RowCount).IsEqualTo(2UL);
        }
        finally
        {
            DeleteIfPresent(path);
        }
    }

    [Test]
    public async Task FixedWidthGeneratedRowWriterTargetsRowGroupsByPrecomputedRowCount()
    {
        using var stream = new MemoryStream();
        var flushedRowCounts = new List<int>();
        using var writer = WideRowSchema.CreateRowWriter(stream, 1, flushedRowCounts.Add,
            new ParquetWriterOptions
            {
                RowApiInitialRowCapacity = 1,
                TargetRowGroupSizeBytes = 521
            });

        for (var i = 0; i < 7; i++)
            writer.GetRow();
        writer.Complete();

        await Assert.That(flushedRowCounts.SequenceEqual([3, 3, 1])).IsTrue();
    }

    [Test]
    public void UncheckedGeneratedReferencesSurviveGrowthAndCompactingGc()
    {
        using var stream = new MemoryStream();
        using (var writer = WideRowSchema.CreateRowWriter(stream, new ParquetWriterOptions
        {
            RowApiMaxParallelism = 1,
            RowApiInitialRowCapacity = 1,
            TargetRowGroupSizeBytes = 65 * sizeof(int) * 32
        }))
        {
            for (var i = 0; i < 16; i++)
            {
                var row = writer.GetRow();
                row.Column0 = CompactAndReturn(i);
                row.Column1 = 1_000 + i;
                row.Column32 = 32_000 + i;
                row.Column63 = 63_000 + i;
                row.Column64 = 64_000 + i;
            }
            writer.Complete();
        }

        using var source = new MemoryReadSource(stream.ToArray());
        using var reader = WideRowSchema.CreateRowReader(source);
        var index = 0;
        while (reader.MoveNext())
        {
            var row = reader.Current;
            if (row.Column0 != index ||
                row.Column1 != 1_000 + index ||
                row.Column32 != 32_000 + index ||
                row.Column63 != 63_000 + index ||
                row.Column64 != 64_000 + index)
            {
                throw new InvalidOperationException($"Unchecked generated row {index} was corrupted.");
            }
            index++;
        }

        if (index != 16)
            throw new InvalidOperationException($"Expected 16 rows, read {index}.");

        using var referenceStream = new MemoryStream();
        using (var writer = CommonClrRowSchema.CreateRowWriter(referenceStream, new ParquetWriterOptions
        {
            RowApiMaxParallelism = 1,
            RowApiInitialRowCapacity = 1,
            TargetRowGroupSizeBytes = 1024 * 1024
        }))
        {
            for (var i = 0; i < 16; i++)
            {
                var row = writer.GetRow();
                row.Name = CompactAndReturn($"row-{i}");
                row.Id = Guid.Empty;
            }
            writer.Complete();
        }

        using var referenceSource = new MemoryReadSource(referenceStream.ToArray());
        using var referenceReader = CommonClrRowSchema.CreateRowReader(referenceSource);
        index = 0;
        while (referenceReader.MoveNext())
        {
            if (referenceReader.Current.Name != $"row-{index}")
                throw new InvalidOperationException($"Unchecked generated reference row {index} was corrupted.");
            index++;
        }

        if (index != 16)
            throw new InvalidOperationException($"Expected 16 reference rows, read {index}.");
    }

    static T CompactAndReturn<T>(T value)
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        return value;
    }

    [Test]
    public async Task GeneratedPipelineWriterCanBeResetToANewStream()
    {
        using var first = new MemoryStream();
        using var second = new MemoryStream();
        using var writer = DatasetRowSchema.CreateRowWriter(first, new ParquetWriterOptions
        {
            RowApiMaxParallelism = 1,
            RowApiInitialRowCapacity = 1,
            TargetRowGroupSizeBytes = 16
        });

        WriteRows(writer, 10);
        writer.Complete();
        writer.Reset(second);
        WriteRows(writer, 5);
        writer.Complete();

        await Assert.That(ReadValues(first).SequenceEqual(Enumerable.Range(0, 10))).IsTrue();
        await Assert.That(ReadValues(second).SequenceEqual(Enumerable.Range(0, 5))).IsTrue();
    }

    [Test]
    public async Task GeneratedPipelineWriterCommitsRowsOnGetRowAndComplete()
    {
        using var stream = new MemoryStream();
        using var writer = DatasetRowSchema.CreateRowWriter(stream, new ParquetWriterOptions
        {
            RowApiMaxParallelism = 1,
            RowApiInitialRowCapacity = 1,
            TargetRowGroupSizeBytes = 16
        });

        for (var i = 0; i < 10; i++)
        {
            var row = writer.GetRow();
            row.Value = i;
            row.Path = ReadOnlyMemory<byte>.Empty;
        }
        writer.Complete();

        await Assert.That(ReadValues(stream).SequenceEqual(Enumerable.Range(0, 10))).IsTrue();
    }

    [Test]
    public async Task RollingRowWriterStartsANewFileAtTheTarget()
    {
        using var files = new RollingFileSet();
        using var writer = DatasetRowSchema.CreateRowWriter(files.SelectPath, files, new ParquetWriterOptions
        {
            RowApiMaxParallelism = 1,
            TargetRowGroupSizeBytes = 16,
            TargetFileSizeBytes = 1
        });
        WriteRows(writer, 10);
        writer.Complete();

        await Assert.That(files.Paths.Count).IsEqualTo(3);
        var values = new List<int>();
        for (var i = 0; i < files.Paths.Count; i++)
        {
            using var stream = File.OpenRead(files.Paths[i]);
            using var reader = DatasetRowSchema.CreateRowReader(stream);
            while (reader.MoveNext())
                values.Add(reader.Current.Value);
        }
        await Assert.That(values.SequenceEqual(Enumerable.Range(0, 10))).IsTrue();
    }

    static void WriteRows(DatasetRowSchema.PipelineWriter writer, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var row = writer.GetRow();
            row.Value = i;
            row.Path = ReadOnlyMemory<byte>.Empty;
        }
    }

    static List<int> ReadValues(MemoryStream stream)
    {
        using var source = new MemoryReadSource(stream.ToArray());
        using var reader = DatasetRowSchema.CreateRowReader(source);
        var values = new List<int>();
        while (reader.MoveNext())
            values.Add(reader.Current.Value);
        return values;
    }

    static string NewPath()
        => Path.Combine(Path.GetTempPath(), $"plank-row-size-{Guid.NewGuid():N}.parquet");

    static void DeleteIfPresent(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    sealed class RollingFileSet : IParquetWriteSource, IDisposable
    {
        FileStream? _stream;
        readonly List<byte[]> _pathsUtf8 = [];

        internal readonly List<string> Paths = [];

        internal ReadOnlySpan<byte> SelectPath(ulong fileIndex, IParquetBufferPool bufferPool,
            out ParquetBuffer? allocation)
        {
            _ = bufferPool;
            allocation = null;
            if (fileIndex != checked((ulong)Paths.Count))
                throw new InvalidOperationException("The rolling file index is not sequential.");
            var path = NewPath();
            Paths.Add(path);
            var pathUtf8 = Encoding.UTF8.GetBytes(path);
            _pathsUtf8.Add(pathUtf8);
            return pathUtf8;
        }

        public void Open(ReadOnlySpan<byte> path, FileMode mode)
        {
            if (_stream is not null)
                throw new InvalidOperationException("The rolling file is already open.");
            _stream = new FileStream(Encoding.UTF8.GetString(path), mode, FileAccess.Write, FileShare.None);
        }

        public void Close()
        {
            _stream?.Dispose();
            _stream = null;
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
        {
            Close();
            for (var i = 0; i < Paths.Count; i++)
                DeleteIfPresent(Paths[i]);
        }

        FileStream GetStream()
            => _stream ?? throw new ObjectDisposedException(nameof(RollingFileSet));
    }
}
