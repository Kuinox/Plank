using ParquetSharp;
using Plank.Schema;
using Plank.Writing;
using PlankColumn = Plank.Schema.Column;

#pragma warning disable CA2007
namespace Plank.Tests;

internal sealed class ParquetSmokeTests
{
    static readonly int[] SampleValues = [10, 20, 30, 40];
    static readonly int[] OtherValues = [1, 2, 3, 4];

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
}
#pragma warning restore CA2007
