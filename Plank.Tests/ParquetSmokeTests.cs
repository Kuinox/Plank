using System.Collections.Immutable;
using ParquetSharp;
using Plank.Schema;
using Plank.Writing;
using PlankColumn = Plank.Schema.Column;

#pragma warning disable CA2007
namespace Plank.Tests;

internal sealed class ParquetSmokeTests
{
    static readonly int[] SampleValues = [10, 20, 30, 40];

    [Test]
    public async Task WriteAndReadSingleIntColumn()
    {
        var path = Path.Combine(Path.GetTempPath(), $"plank-smoke-{Guid.NewGuid():N}.parquet");
        try
        {
            var schema = new ParquetSchema(ImmutableArray.Create(
                new PlankColumn("A", ParquetPhysicalType.Int32, ColumnOptions.Default)));
            await using (var stream = File.Create(path))
            await using (var writer = ParquetWriter.Create(stream, schema))
            {
                var rowGroup = writer.StartRowGroup();
                await rowGroup.WriteAsync(schema.Columns[0], SampleValues).ConfigureAwait(false);
                await writer.CompleteAsync().ConfigureAwait(false);
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
}
#pragma warning restore CA2007
