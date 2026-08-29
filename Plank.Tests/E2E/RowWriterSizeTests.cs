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
        writer.Next();

        writer.Dispose();
        writer.Dispose();

        Assert.Throws<ObjectDisposedException>(() => writer.GetRow());
        Assert.Throws<ObjectDisposedException>(() => writer.Next());
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
            writer.Next();
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
