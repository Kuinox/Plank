using ParquetSharp;
using Plank.Schema;
using Plank.Writing;
using PlankColumn = Plank.Schema.Column;

#pragma warning disable CA2007
namespace Plank.Tests;

internal sealed class ParquetSmokeTests
{
    const bool KeepArtifacts = false;
    static readonly int[] SampleValues = [10, 20, 30, 40];
    static readonly int[] OtherValues = [1, 2, 3, 4];
    static readonly int[] SampleManyValues = [.. Enumerable.Range(0, 2048)];
    static readonly int[][] SampleRepeatedIntValues =
    [
        [1, 2, 3],
        [],
        [4],
        [5, 6]
    ];
    static readonly int?[][] SampleRepeatedNullableIntValues =
    [
        [1, null, 3],
        [],
        [null],
        [4]
    ];
    static readonly long[] SampleInt64Values = [10L, 20L, 30L, 40L];
    static readonly float[] SampleFloatValues = [1.25f, -2.5f, 0.75f, 9.5f];
    static readonly double[] SampleDoubleValues = [10.125, -20.5, 30.75, 40.875];
    static readonly bool[] SampleBooleanValues = [true, false, true, true, false, false, true, false, true];
    static readonly string[] SampleStringValues = ["hello", "caf\u00E9", "na\u00EFve", "emoji \U0001F642"];
    static readonly string[] SampleManyStringValues = [.. Enumerable.Range(0, 256).Select(i => new string((char)('a' + (i % 26)), (i % 31) + 1))];
    static readonly DateOnly[] SampleDateOnlyValues =
    [
        new DateOnly(2026, 2, 1),
        new DateOnly(2026, 2, 2),
        new DateOnly(2026, 2, 3)
    ];
    static readonly TimeOnly[] SampleTimeOnlyValues =
    [
        new TimeOnly(0, 0, 0),
        new TimeOnly(10, 30, 15, 250),
        new TimeOnly(23, 59, 59, 999)
    ];

    static void Cleanup(string path)
    {
        if (KeepArtifacts || !File.Exists(path))
            return;

        File.Delete(path);
    }
    static readonly DateTime[] SampleUtcDateTimeValues =
    [
        new DateTime(2026, 2, 1, 10, 30, 0, DateTimeKind.Utc),
        new DateTime(2026, 2, 1, 10, 30, 1, DateTimeKind.Utc),
        new DateTime(2026, 2, 1, 10, 30, 2, DateTimeKind.Utc)
    ];
    static readonly DateTimeOffset[] SampleDateTimeOffsetValues =
    [
        new DateTimeOffset(2026, 2, 1, 10, 30, 0, TimeSpan.FromHours(+2)),
        new DateTimeOffset(2026, 2, 1, 8, 30, 1, TimeSpan.Zero),
        new DateTimeOffset(2026, 2, 1, 3, 30, 2, TimeSpan.FromHours(-5))
    ];

    [Test]
    public async Task WriteAndReadSingleIntColumn()
    {
        var path = Path.Combine(Path.GetTempPath(), $"plank-smoke-{Guid.NewGuid():N}.parquet");
        try
        {
            var schema = new ParquetSchema([
                new PlankColumn("A", ParquetPhysicalType.Int32, ColumnOptions.Default)
            ]);
            await using (var stream = File.Create(path))
            using (var writer = ParquetWriter.Create(stream, schema))
            {
                var rowGroup = writer.StartRowGroup();
                await rowGroup.WriteAsync(schema.Columns[0], SampleValues).ConfigureAwait(false);
                writer.CloseFile();
            }

            using var fileReader = new ParquetFileReader(path);
            await Assert.That(fileReader.FileMetaData.NumRowGroups).IsEqualTo(1);
            await Assert.That(fileReader.FileMetaData.NumRows).IsEqualTo(4L);

            using var rowGroupReader = fileReader.RowGroup(0);
            var rowCount = checked((int)rowGroupReader.MetaData.NumRows);
            using var columnReader = rowGroupReader.Column(0).LogicalReader<int>();
            var values = columnReader.ReadAll(rowCount);

            await Assert.That(values.SequenceEqual(SampleValues)).IsTrue();
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Test]
    public async Task WriteAsyncCompletesWhenColumnIsWrittenInOrder()
    {
        using var stream = new MemoryStream();
        var schema = new ParquetSchema([
            new PlankColumn("A", ParquetPhysicalType.Int32, ColumnOptions.Default),
            new PlankColumn("B", ParquetPhysicalType.Int32, ColumnOptions.Default)
        ]);
        using var writer = ParquetWriter.Create(stream, schema, new ParquetWriterOptions
        {
            ExpectedRowGroupCount = 1,
            RowGroupRowCountHint = (uint)SampleValues.Length
        });

        var rowGroup = writer.StartRowGroup();
        var secondWrite = rowGroup.WriteAsync(schema.Columns[1], OtherValues);
        await Assert.That(secondWrite.IsCompleted).IsFalse();
        await rowGroup.WriteAsync(schema.Columns[0], SampleValues);
        await secondWrite;
        writer.CloseFile();
    }

    [Test]
    public async Task WriteAsyncSupportsParallelColumnWrites()
    {
        var path = Path.Combine(Path.GetTempPath(), $"plank-parallel-{Guid.NewGuid():N}.parquet");
        try
        {
            var schema = new ParquetSchema([
                new PlankColumn("A", ParquetPhysicalType.Int32, ColumnOptions.Default),
                new PlankColumn("B", ParquetPhysicalType.Int32, ColumnOptions.Default)
            ]);
            await using (var stream = File.Create(path))
            using (var writer = ParquetWriter.Create(stream, schema))
            {
                var rowGroup = writer.StartRowGroup();
                var writeA = rowGroup.WriteAsync(schema.Columns[0], SampleValues);
                var writeB = rowGroup.WriteAsync(schema.Columns[1], OtherValues);
                await Task.WhenAll(writeA.AsTask(), writeB.AsTask()).ConfigureAwait(false);
                writer.CloseFile();
            }

            using var fileReader = new ParquetFileReader(path);
            await Assert.That(fileReader.FileMetaData.NumRowGroups).IsEqualTo(1);
            await Assert.That(fileReader.FileMetaData.NumRows).IsEqualTo(4L);

            using var rowGroupReader = fileReader.RowGroup(0);
            var rowCount = checked((int)rowGroupReader.MetaData.NumRows);
            using var readerA = rowGroupReader.Column(0).LogicalReader<int>();
            using var readerB = rowGroupReader.Column(1).LogicalReader<int>();
            var valuesA = readerA.ReadAll(rowCount);
            var valuesB = readerB.ReadAll(rowCount);

            await Assert.That(valuesA.SequenceEqual(SampleValues)).IsTrue();
            await Assert.That(valuesB.SequenceEqual(OtherValues)).IsTrue();
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Test]
    public async Task WriteAndReadSingleIntColumnWithGzipCompression()
    {
        var path = Path.Combine(Path.GetTempPath(), $"plank-smoke-gzip-{Guid.NewGuid():N}.parquet");
        try
        {
            var schema = new ParquetSchema([
                new PlankColumn("A", ParquetPhysicalType.Int32, ColumnOptions.Default)
            ]);
            await using (var stream = File.Create(path))
            using (var writer = ParquetWriter.Create(stream, schema, new ParquetWriterOptions
            {
                Compression = CompressionKind.Gzip
            }))
            {
                var rowGroup = writer.StartRowGroup();
                await rowGroup.WriteAsync(schema.Columns[0], SampleManyValues).ConfigureAwait(false);
                writer.CloseFile();
            }

            using var fileReader = new ParquetFileReader(path);
            using var rowGroupReader = fileReader.RowGroup(0);
            using var columnReader = rowGroupReader.Column(0).LogicalReader<int>();
            var values = columnReader.ReadAll(SampleManyValues.Length);
            await Assert.That(values.SequenceEqual(SampleManyValues)).IsTrue();
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Test]
    public async Task WriteAndReadSingleIntColumnWithBrotliCompression()
    {
        var path = Path.Combine(Path.GetTempPath(), $"plank-smoke-brotli-{Guid.NewGuid():N}.parquet");
        try
        {
            var schema = new ParquetSchema([
                new PlankColumn("A", ParquetPhysicalType.Int32, ColumnOptions.Default)
            ]);
            await using (var stream = File.Create(path))
            using (var writer = ParquetWriter.Create(stream, schema, new ParquetWriterOptions
            {
                Compression = CompressionKind.Brotli
            }))
            {
                var rowGroup = writer.StartRowGroup();
                await rowGroup.WriteAsync(schema.Columns[0], SampleManyValues).ConfigureAwait(false);
                writer.CloseFile();
            }

            using var fileReader = new ParquetFileReader(path);
            using var rowGroupReader = fileReader.RowGroup(0);
            using var columnReader = rowGroupReader.Column(0).LogicalReader<int>();
            var values = columnReader.ReadAll(SampleManyValues.Length);
            await Assert.That(values.SequenceEqual(SampleManyValues)).IsTrue();
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Test]
    public async Task WriteAndReadSingleIntColumnWithSnappyCompression()
    {
        var path = Path.Combine(Path.GetTempPath(), $"plank-smoke-snappy-{Guid.NewGuid():N}.parquet");
        try
        {
            var schema = new ParquetSchema([
                new PlankColumn("A", ParquetPhysicalType.Int32, ColumnOptions.Default)
            ]);
            await using (var stream = File.Create(path))
            using (var writer = ParquetWriter.Create(stream, schema, new ParquetWriterOptions
            {
                Compression = CompressionKind.Snappy
            }))
            {
                var rowGroup = writer.StartRowGroup();
                await rowGroup.WriteAsync(schema.Columns[0], SampleManyValues).ConfigureAwait(false);
                writer.CloseFile();
            }

            using var fileReader = new ParquetFileReader(path);
            using var rowGroupReader = fileReader.RowGroup(0);
            using var columnReader = rowGroupReader.Column(0).LogicalReader<int>();
            var values = columnReader.ReadAll(SampleManyValues.Length);
            await Assert.That(values.SequenceEqual(SampleManyValues)).IsTrue();
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Test]
    public async Task WriteAndReadSingleIntColumnWithLz4Compression()
    {
        var path = Path.Combine(Path.GetTempPath(), $"plank-smoke-lz4-{Guid.NewGuid():N}.parquet");
        try
        {
            var schema = new ParquetSchema([
                new PlankColumn("A", ParquetPhysicalType.Int32, ColumnOptions.Default)
            ]);
            await using (var stream = File.Create(path))
            using (var writer = ParquetWriter.Create(stream, schema, new ParquetWriterOptions
            {
                Compression = CompressionKind.Lz4
            }))
            {
                var rowGroup = writer.StartRowGroup();
                await rowGroup.WriteAsync(schema.Columns[0], SampleManyValues).ConfigureAwait(false);
                writer.CloseFile();
            }

            using var fileReader = new ParquetFileReader(path);
            using var rowGroupReader = fileReader.RowGroup(0);
            using var columnReader = rowGroupReader.Column(0).LogicalReader<int>();
            var values = columnReader.ReadAll(SampleManyValues.Length);
            await Assert.That(values.SequenceEqual(SampleManyValues)).IsTrue();
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Test]
    public async Task WriteAndReadSingleIntColumnWithZstdCompression()
    {
        var path = Path.Combine(Path.GetTempPath(), $"plank-smoke-zstd-{Guid.NewGuid():N}.parquet");
        try
        {
            var schema = new ParquetSchema([
                new PlankColumn("A", ParquetPhysicalType.Int32, ColumnOptions.Default)
            ]);
            await using (var stream = File.Create(path))
            using (var writer = ParquetWriter.Create(stream, schema, new ParquetWriterOptions
            {
                Compression = CompressionKind.Zstd
            }))
            {
                var rowGroup = writer.StartRowGroup();
                await rowGroup.WriteAsync(schema.Columns[0], SampleManyValues).ConfigureAwait(false);
                writer.CloseFile();
            }

            using var fileReader = new ParquetFileReader(path);
            using var rowGroupReader = fileReader.RowGroup(0);
            using var columnReader = rowGroupReader.Column(0).LogicalReader<int>();
            var values = columnReader.ReadAll(SampleManyValues.Length);
            await Assert.That(values.SequenceEqual(SampleManyValues)).IsTrue();
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Test]
    public async Task SplittingPagesStillProducesReadableFile()
    {
        var splitPath = Path.Combine(Path.GetTempPath(), $"plank-smoke-split-{Guid.NewGuid():N}.parquet");
        var singlePath = Path.Combine(Path.GetTempPath(), $"plank-smoke-single-{Guid.NewGuid():N}.parquet");
        try
        {
            var schema = new ParquetSchema([
                new PlankColumn("A", ParquetPhysicalType.Int32, ColumnOptions.Default)
            ]);
            await using (var splitStream = File.Create(splitPath))
            using (var splitWriter = ParquetWriter.Create(splitStream, schema, new ParquetWriterOptions
            {
                RowGroupOptions = new RowGroupOptions
                {
                    MaxCompressedBytes = 4 * 1024 * 1024,
                    MaxPageValueCount = 64
                }
            }))
            {
                var rowGroup = splitWriter.StartRowGroup();
                await rowGroup.WriteAsync(schema.Columns[0], SampleManyValues).ConfigureAwait(false);
                splitWriter.CloseFile();
            }

            await using (var singleStream = File.Create(singlePath))
            using (var singleWriter = ParquetWriter.Create(singleStream, schema))
            {
                var rowGroup = singleWriter.StartRowGroup();
                await rowGroup.WriteAsync(schema.Columns[0], SampleManyValues).ConfigureAwait(false);
                singleWriter.CloseFile();
            }

            using var splitReader = new ParquetFileReader(splitPath);
            using var splitRowGroup = splitReader.RowGroup(0);
            using var splitColumn = splitRowGroup.Column(0).LogicalReader<int>();
            var splitValues = splitColumn.ReadAll(SampleManyValues.Length);
            await Assert.That(splitValues.SequenceEqual(SampleManyValues)).IsTrue();

            var splitInfo = new FileInfo(splitPath);
            var singleInfo = new FileInfo(singlePath);
            await Assert.That(splitInfo.Length > singleInfo.Length).IsTrue();
        }
        finally
        {
            Cleanup(splitPath);
            Cleanup(singlePath);
        }
    }

    [Test]
    public async Task SplittingVariableWidthPagesStillProducesReadableFile()
    {
        var splitPath = Path.Combine(Path.GetTempPath(), $"plank-smoke-split-var-{Guid.NewGuid():N}.parquet");
        var singlePath = Path.Combine(Path.GetTempPath(), $"plank-smoke-single-var-{Guid.NewGuid():N}.parquet");
        try
        {
            var schema = new ParquetSchema([
                new PlankColumn("A", ParquetPhysicalType.ByteArray, ColumnOptions.Default)
            ]);
            await using (var splitStream = File.Create(splitPath))
            using (var splitWriter = ParquetWriter.Create(splitStream, schema, new ParquetWriterOptions
            {
                RowGroupOptions = new RowGroupOptions
                {
                    MaxCompressedBytes = 4 * 1024 * 1024,
                    MaxPageValueCount = 16,
                    MaxPageBytes = 128
                }
            }))
            {
                var rowGroup = splitWriter.StartRowGroup();
                await rowGroup.WriteAsync(schema.Columns[0], SampleManyStringValues).ConfigureAwait(false);
                splitWriter.CloseFile();
            }

            await using (var singleStream = File.Create(singlePath))
            using (var singleWriter = ParquetWriter.Create(singleStream, schema))
            {
                var rowGroup = singleWriter.StartRowGroup();
                await rowGroup.WriteAsync(schema.Columns[0], SampleManyStringValues).ConfigureAwait(false);
                singleWriter.CloseFile();
            }

            using var splitReader = new ParquetFileReader(splitPath);
            using var splitRowGroup = splitReader.RowGroup(0);
            using var splitColumn = splitRowGroup.Column(0).LogicalReader<string>();
            var splitValues = splitColumn.ReadAll(SampleManyStringValues.Length);
            await Assert.That(splitValues.SequenceEqual(SampleManyStringValues)).IsTrue();

            var splitInfo = new FileInfo(splitPath);
            var singleInfo = new FileInfo(singlePath);
            await Assert.That(splitInfo.Length > singleInfo.Length).IsTrue();
        }
        finally
        {
            Cleanup(splitPath);
            Cleanup(singlePath);
        }
    }

    [Test]
    public async Task WriteAndReadRepeatedIntColumn()
    {
        var path = Path.Combine(Path.GetTempPath(), $"plank-smoke-repeated-int-{Guid.NewGuid():N}.parquet");
        try
        {
            var schema = new ParquetSchema([
                new PlankColumn("A", ParquetPhysicalType.Int32, new ColumnOptions(ParquetRepetition.Repeated, []))
            ]);
            await using (var stream = File.Create(path))
            using (var writer = ParquetWriter.Create(stream, schema))
            {
                var rowGroup = writer.StartRowGroup();
                await rowGroup.WriteAsync(schema.Columns[0], new RepeatedValues<int>(SampleRepeatedIntValues)).ConfigureAwait(false);
                writer.CloseFile();
            }

            using var fileReader = new ParquetFileReader(path);
            await Assert.That(fileReader.FileMetaData.NumRowGroups).IsEqualTo(1);
            await Assert.That(fileReader.FileMetaData.NumRows).IsEqualTo(SampleRepeatedIntValues.Length);

            using var rowGroupReader = fileReader.RowGroup(0);
            using var columnReader = rowGroupReader.Column(0).LogicalReader<int[]>();
            var values = columnReader.ReadAll(SampleRepeatedIntValues.Length);
            await Assert.That(values.Length).IsEqualTo(SampleRepeatedIntValues.Length);
            for (var i = 0; i < values.Length; i++)
                await Assert.That(values[i].SequenceEqual(SampleRepeatedIntValues[i])).IsTrue();
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Test]
    public async Task WriteAndReadRepeatedNullableIntColumn()
    {
        var path = Path.Combine(Path.GetTempPath(), $"plank-smoke-repeated-nullable-int-{Guid.NewGuid():N}.parquet");
        try
        {
            var schema = new ParquetSchema([
                new PlankColumn("A", ParquetPhysicalType.Int32, new ColumnOptions(ParquetRepetition.Repeated, []))
            ]);
            await using (var stream = File.Create(path))
            using (var writer = ParquetWriter.Create(stream, schema))
            {
                var rowGroup = writer.StartRowGroup();
                await rowGroup.WriteAsync(schema.Columns[0], new RepeatedValues<int?>(SampleRepeatedNullableIntValues)).ConfigureAwait(false);
                writer.CloseFile();
            }

            using var fileReader = new ParquetFileReader(path);
            using var rowGroupReader = fileReader.RowGroup(0);
            using var columnReader = rowGroupReader.Column(0).LogicalReader<int?[]>();
            var values = columnReader.ReadAll(SampleRepeatedNullableIntValues.Length);
            await Assert.That(values.Length).IsEqualTo(SampleRepeatedNullableIntValues.Length);
            for (var i = 0; i < values.Length; i++)
                await Assert.That(values[i].SequenceEqual(SampleRepeatedNullableIntValues[i])).IsTrue();
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Test]
    public async Task PreserializedColumnsCanBeWrittenToLaterRowGroups()
    {
        var path = Path.Combine(Path.GetTempPath(), $"plank-preserialized-{Guid.NewGuid():N}.parquet");
        try
        {
            var schema = new ParquetSchema([
                new PlankColumn("A", ParquetPhysicalType.Int32, ColumnOptions.Default)
            ]);
            var values1 = new[] { 7, 8, 9 };
            var values2 = new[] { 10, 11, 12 };
            var buffer1 = new byte[values1.Length * sizeof(int)];
            var buffer2 = new byte[values2.Length * sizeof(int)];

            await using (var stream = File.Create(path))
            using (var writer = ParquetWriter.Create(stream, schema, new ParquetWriterOptions
            {
                ExpectedRowGroupCount = 2,
                RowGroupRowCountHint = 3u
            }))
            {
                var serialized1 = writer.SerializeColumn(schema.Columns[0], values1, buffer1);
                var serialized2 = writer.SerializeColumn(schema.Columns[0], values2, buffer2);

                var rg1 = writer.StartRowGroup();
                await rg1.WriteAsync(serialized1).ConfigureAwait(false);

                var rg2 = writer.StartRowGroup();
                await rg2.WriteAsync(serialized2).ConfigureAwait(false);

                writer.CloseFile();
            }

            using var fileReader = new ParquetFileReader(path);
            await Assert.That(fileReader.FileMetaData.NumRowGroups).IsEqualTo(2);
            await Assert.That(fileReader.FileMetaData.NumRows).IsEqualTo(6L);

            using var rgReader1 = fileReader.RowGroup(0);
            using var rgReader2 = fileReader.RowGroup(1);
            using var colReader1 = rgReader1.Column(0).LogicalReader<int>();
            using var colReader2 = rgReader2.Column(0).LogicalReader<int>();
            var read1 = colReader1.ReadAll(3);
            var read2 = colReader2.ReadAll(3);

            await Assert.That(read1.SequenceEqual(values1)).IsTrue();
            await Assert.That(read2.SequenceEqual(values2)).IsTrue();
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Test]
    public async Task WriteAndReadSingleInt64Column()
    {
        var path = Path.Combine(Path.GetTempPath(), $"plank-smoke-int64-{Guid.NewGuid():N}.parquet");
        try
        {
            var schema = new ParquetSchema([
                new PlankColumn("A", ParquetPhysicalType.Int64, ColumnOptions.Default)
            ]);
            await using (var stream = File.Create(path))
            using (var writer = ParquetWriter.Create(stream, schema))
            {
                var rowGroup = writer.StartRowGroup();
                await rowGroup.WriteAsync(schema.Columns[0], SampleInt64Values).ConfigureAwait(false);
                writer.CloseFile();
            }

            using var fileReader = new ParquetFileReader(path);
            using var rowGroupReader = fileReader.RowGroup(0);
            var rowCount = checked((int)rowGroupReader.MetaData.NumRows);
            using var columnReader = rowGroupReader.Column(0).LogicalReader<long>();
            var values = columnReader.ReadAll(rowCount);

            await Assert.That(values.SequenceEqual(SampleInt64Values)).IsTrue();
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Test]
    public async Task WriteAndReadSingleFloatColumn()
    {
        var path = Path.Combine(Path.GetTempPath(), $"plank-smoke-float-{Guid.NewGuid():N}.parquet");
        try
        {
            var schema = new ParquetSchema([
                new PlankColumn("A", ParquetPhysicalType.Float, ColumnOptions.Default)
            ]);
            await using (var stream = File.Create(path))
            using (var writer = ParquetWriter.Create(stream, schema))
            {
                var rowGroup = writer.StartRowGroup();
                await rowGroup.WriteAsync(schema.Columns[0], SampleFloatValues).ConfigureAwait(false);
                writer.CloseFile();
            }

            using var fileReader = new ParquetFileReader(path);
            using var rowGroupReader = fileReader.RowGroup(0);
            var rowCount = checked((int)rowGroupReader.MetaData.NumRows);
            using var columnReader = rowGroupReader.Column(0).LogicalReader<float>();
            var values = columnReader.ReadAll(rowCount);

            await Assert.That(values.SequenceEqual(SampleFloatValues)).IsTrue();
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Test]
    public async Task WriteAndReadSingleDoubleColumn()
    {
        var path = Path.Combine(Path.GetTempPath(), $"plank-smoke-double-{Guid.NewGuid():N}.parquet");
        try
        {
            var schema = new ParquetSchema([
                new PlankColumn("A", ParquetPhysicalType.Double, ColumnOptions.Default)
            ]);
            await using (var stream = File.Create(path))
            using (var writer = ParquetWriter.Create(stream, schema))
            {
                var rowGroup = writer.StartRowGroup();
                await rowGroup.WriteAsync(schema.Columns[0], SampleDoubleValues).ConfigureAwait(false);
                writer.CloseFile();
            }

            using var fileReader = new ParquetFileReader(path);
            using var rowGroupReader = fileReader.RowGroup(0);
            var rowCount = checked((int)rowGroupReader.MetaData.NumRows);
            using var columnReader = rowGroupReader.Column(0).LogicalReader<double>();
            var values = columnReader.ReadAll(rowCount);

            await Assert.That(values.SequenceEqual(SampleDoubleValues)).IsTrue();
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Test]
    public async Task WriteAndReadSingleBooleanColumn()
    {
        var path = Path.Combine(Path.GetTempPath(), $"plank-smoke-bool-{Guid.NewGuid():N}.parquet");
        try
        {
            var schema = new ParquetSchema([
                new PlankColumn("A", ParquetPhysicalType.Boolean, ColumnOptions.Default)
            ]);
            await using (var stream = File.Create(path))
            using (var writer = ParquetWriter.Create(stream, schema))
            {
                var rowGroup = writer.StartRowGroup();
                await rowGroup.WriteAsync(schema.Columns[0], SampleBooleanValues).ConfigureAwait(false);
                writer.CloseFile();
            }

            using var fileReader = new ParquetFileReader(path);
            using var rowGroupReader = fileReader.RowGroup(0);
            var rowCount = checked((int)rowGroupReader.MetaData.NumRows);
            using var columnReader = rowGroupReader.Column(0).LogicalReader<bool>();
            var values = columnReader.ReadAll(rowCount);

            await Assert.That(values.SequenceEqual(SampleBooleanValues)).IsTrue();
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Test]
    public async Task WriteAndReadSingleDateTimeColumnAsUnixMicros()
    {
        var path = Path.Combine(Path.GetTempPath(), $"plank-smoke-datetime-{Guid.NewGuid():N}.parquet");
        try
        {
            var schema = new ParquetSchema([
                new PlankColumn("A", ParquetPhysicalType.Int64, ColumnOptions.Default)
            ]);
            await using (var stream = File.Create(path))
            using (var writer = ParquetWriter.Create(stream, schema))
            {
                var rowGroup = writer.StartRowGroup();
                await rowGroup.WriteAsync(schema.Columns[0], SampleUtcDateTimeValues).ConfigureAwait(false);
                writer.CloseFile();
            }

            using var fileReader = new ParquetFileReader(path);
            using var rowGroupReader = fileReader.RowGroup(0);
            var rowCount = checked((int)rowGroupReader.MetaData.NumRows);
            using var columnReader = rowGroupReader.Column(0).LogicalReader<DateTime>();
            var values = columnReader.ReadAll(rowCount);
            await Assert.That(values.Select(v => v.Ticks).SequenceEqual(SampleUtcDateTimeValues.Select(v => v.Ticks))).IsTrue();
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Test]
    public async Task DateTimeRequireUtcRejectsLocalAndUnspecified()
    {
        using var stream = new MemoryStream();
        var schema = new ParquetSchema([
            new PlankColumn("A", ParquetPhysicalType.Int64, ColumnOptions.Default)
        ]);
        using var writer = ParquetWriter.Create(stream, schema, new ParquetWriterOptions
        {
            ExpectedRowGroupCount = 1,
            RowGroupRowCountHint = 1
        });
        var rowGroup = writer.StartRowGroup();

        var local = new[] { new DateTime(2026, 2, 1, 10, 0, 0, DateTimeKind.Local) };
        var unspecified = new[] { new DateTime(2026, 2, 1, 10, 0, 0, DateTimeKind.Unspecified) };

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await rowGroup.WriteAsync(schema.Columns[0], local));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await rowGroup.WriteAsync(schema.Columns[0], unspecified));
    }

    [Test]
    public async Task DateTimeFlagsCanConvertLocalAndAssumeUnspecifiedAsUtc()
    {
        var path = Path.Combine(Path.GetTempPath(), $"plank-smoke-datetime-flags-{Guid.NewGuid():N}.parquet");
        try
        {
            var schema = new ParquetSchema([
                new PlankColumn("A", ParquetPhysicalType.Int64, ColumnOptions.Default)
            ]);
            var local = new DateTime(2026, 2, 1, 10, 0, 0, DateTimeKind.Local);
            var unspecified = new DateTime(2026, 2, 1, 12, 0, 0, DateTimeKind.Unspecified);
            var utc = new DateTime(2026, 2, 1, 14, 0, 0, DateTimeKind.Utc);
            var valuesToWrite = new[] { local, unspecified, utc };

            await using (var stream = File.Create(path))
            using (var writer = ParquetWriter.Create(stream, schema, new ParquetWriterOptions
            {
                DateTimeKindHandling = DateTimeKindHandling.ConvertLocalToUtc | DateTimeKindHandling.AssumeUnspecifiedAsUtc
            }))
            {
                var rowGroup = writer.StartRowGroup();
                await rowGroup.WriteAsync(schema.Columns[0], valuesToWrite).ConfigureAwait(false);
                writer.CloseFile();
            }

            using var fileReader = new ParquetFileReader(path);
            using var rowGroupReader = fileReader.RowGroup(0);
            using var columnReader = rowGroupReader.Column(0).LogicalReader<DateTime>();
            var encoded = columnReader.ReadAll(3);
            var expected = new[]
            {
                local.ToUniversalTime().Ticks,
                DateTime.SpecifyKind(unspecified, DateTimeKind.Utc).Ticks,
                utc.Ticks
            };

            await Assert.That(encoded.Select(v => v.Ticks).SequenceEqual(expected)).IsTrue();
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Test]
    public async Task DateTimeColumnCanBeReadAsLogicalDateTime()
    {
        var path = Path.Combine(Path.GetTempPath(), $"plank-smoke-datetime-logical-{Guid.NewGuid():N}.parquet");
        try
        {
            var schema = new ParquetSchema([
                new PlankColumn("A", ParquetPhysicalType.Int64, ColumnOptions.Default)
            ]);
            await using (var stream = File.Create(path))
            using (var writer = ParquetWriter.Create(stream, schema))
            {
                var rowGroup = writer.StartRowGroup();
                await rowGroup.WriteAsync(schema.Columns[0], SampleUtcDateTimeValues).ConfigureAwait(false);
                writer.CloseFile();
            }

            using var fileReader = new ParquetFileReader(path);
            using var rowGroupReader = fileReader.RowGroup(0);
            using var columnReader = rowGroupReader.Column(0).LogicalReader<DateTime>();
            var values = columnReader.ReadAll(SampleUtcDateTimeValues.Length);

            await Assert.That(values.Select(v => v.Ticks).SequenceEqual(SampleUtcDateTimeValues.Select(v => v.Ticks))).IsTrue();
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Test]
    public async Task DateTimeOffsetColumnCanBeReadAsLogicalDateTime()
    {
        var path = Path.Combine(Path.GetTempPath(), $"plank-smoke-datetimeoffset-logical-{Guid.NewGuid():N}.parquet");
        try
        {
            var schema = new ParquetSchema([
                new PlankColumn("A", ParquetPhysicalType.Int64, ColumnOptions.Default)
            ]);
            await using (var stream = File.Create(path))
            using (var writer = ParquetWriter.Create(stream, schema))
            {
                var rowGroup = writer.StartRowGroup();
                await rowGroup.WriteAsync(schema.Columns[0], SampleDateTimeOffsetValues).ConfigureAwait(false);
                writer.CloseFile();
            }

            using var fileReader = new ParquetFileReader(path);
            using var rowGroupReader = fileReader.RowGroup(0);
            using var columnReader = rowGroupReader.Column(0).LogicalReader<DateTime>();
            var values = columnReader.ReadAll(SampleDateTimeOffsetValues.Length);
            var expectedTicks = SampleDateTimeOffsetValues.Select(v => v.UtcTicks);

            await Assert.That(values.Select(v => v.Ticks).SequenceEqual(expectedTicks)).IsTrue();
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Test]
    public async Task WriteAndReadSingleStringColumn()
    {
        var path = Path.Combine(Path.GetTempPath(), $"plank-smoke-string-{Guid.NewGuid():N}.parquet");
        try
        {
            var schema = new ParquetSchema([
                new PlankColumn("A", ParquetPhysicalType.ByteArray, ColumnOptions.Default)
            ]);
            await using (var stream = File.Create(path))
            using (var writer = ParquetWriter.Create(stream, schema))
            {
                var rowGroup = writer.StartRowGroup();
                await rowGroup.WriteAsync(schema.Columns[0], SampleStringValues).ConfigureAwait(false);
                writer.CloseFile();
            }

            using var fileReader = new ParquetFileReader(path);
            using var rowGroupReader = fileReader.RowGroup(0);
            using var columnReader = rowGroupReader.Column(0).LogicalReader<string>();
            var values = columnReader.ReadAll(SampleStringValues.Length);

            await Assert.That(values.SequenceEqual(SampleStringValues)).IsTrue();
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Test]
    public async Task WriteAndReadSingleDateOnlyColumn()
    {
        var path = Path.Combine(Path.GetTempPath(), $"plank-smoke-dateonly-{Guid.NewGuid():N}.parquet");
        try
        {
            var schema = new ParquetSchema([
                new PlankColumn("A", ParquetPhysicalType.Int32, ColumnOptions.Default)
            ]);
            await using (var stream = File.Create(path))
            using (var writer = ParquetWriter.Create(stream, schema))
            {
                var rowGroup = writer.StartRowGroup();
                await rowGroup.WriteAsync(schema.Columns[0], SampleDateOnlyValues).ConfigureAwait(false);
                writer.CloseFile();
            }

            using var fileReader = new ParquetFileReader(path);
            fileReader.LogicalTypeFactory.DateAsDateOnly = true;
            using var rowGroupReader = fileReader.RowGroup(0);
            using var columnReader = rowGroupReader.Column(0).LogicalReader<DateOnly>();
            var values = columnReader.ReadAll(SampleDateOnlyValues.Length);

            await Assert.That(values.SequenceEqual(SampleDateOnlyValues)).IsTrue();
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Test]
    public async Task WriteAndReadSingleTimeOnlyColumn()
    {
        var path = Path.Combine(Path.GetTempPath(), $"plank-smoke-timeonly-{Guid.NewGuid():N}.parquet");
        try
        {
            var schema = new ParquetSchema([
                new PlankColumn("A", ParquetPhysicalType.Int64, ColumnOptions.Default)
            ]);
            await using (var stream = File.Create(path))
            using (var writer = ParquetWriter.Create(stream, schema))
            {
                var rowGroup = writer.StartRowGroup();
                await rowGroup.WriteAsync(schema.Columns[0], SampleTimeOnlyValues).ConfigureAwait(false);
                writer.CloseFile();
            }

            using var fileReader = new ParquetFileReader(path);
            fileReader.LogicalTypeFactory.TimeAsTimeOnly = true;
            using var rowGroupReader = fileReader.RowGroup(0);
            using var columnReader = rowGroupReader.Column(0).LogicalReader<TimeOnly>();
            var values = columnReader.ReadAll(SampleTimeOnlyValues.Length);

            await Assert.That(values.SequenceEqual(SampleTimeOnlyValues)).IsTrue();
        }
        finally
        {
            Cleanup(path);
        }
    }
}
#pragma warning restore CA2007
