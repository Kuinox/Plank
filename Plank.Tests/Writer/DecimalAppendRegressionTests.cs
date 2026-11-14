using System.Collections.Immutable;
using System.Text;
using Plank.Reading.Physical;
using Plank.Schema;
using Plank.Writing;

namespace Plank.Tests.Writer;

internal sealed class DecimalAppendRegressionTests
{
    [Test]
    [Arguments(ParquetPhysicalType.Int32)]
    [Arguments(ParquetPhysicalType.Int64)]
    [Arguments(ParquetPhysicalType.ByteArray)]
    [Arguments(ParquetPhysicalType.FixedLenByteArray)]
    public async Task AppendingDecimalsRetainsValuesAndSorting(ParquetPhysicalType physicalType)
    {
        var schema = new ParquetSchema([
            ColumnDefinition.OptionalLeaf("Value", physicalType,
                new ColumnOptions(encodings: [EncodingKind.Plain],
                    typeLength: physicalType == ParquetPhysicalType.FixedLenByteArray ? 12U : 0U),
                logicalType: new LogicalType.Decimal(9, 2))
        ]);
        await CheckAppendedSorting<decimal?>(schema, new ParquetSortingColumn(0, nullsFirst: true),
            [null, -2.25m, 0m], [3.5m], true).ConfigureAwait(false);
    }

    static async Task CheckAppendedSorting<T>(ParquetSchema schema, ParquetSortingColumn sorting,
        T[] retained, T[] incoming, bool expectedSorting,
        ParquetWriterOptions? sourceOptions = null, ParquetWriterOptions? appendWriterOptions = null)
    {
        var path = NewPath();
        try
        {
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var writer = schema.CreateWriter(stream, sourceOptions ?? new ParquetWriterOptions { SortingColumns = [sorting] });
                var column = writer.CreateSerializedColumn<T>(schema.LeafColumns[0]);
                column.Serialize(retained);
                writer.StartRowGroup().Write(column);
                writer.CloseFile();
            }
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var writer = schema.CreateAppender(stream, new ParquetAppendOptions
                {
                    AppendToLatestRowGroup = true,
                    WriterOptions = appendWriterOptions ?? ParquetWriterOptions.Default
                });
                var column = writer.CreateSerializedColumn<T>(schema.LeafColumns[0]);
                column.Serialize(incoming);
                writer.StartRowGroup().Write(column);
                writer.CloseFile();
            }
            using var reader = new ParquetFileReader();
            using var readStream = File.OpenRead(path);
            reader.Reset(readStream);
            await Assert.That(reader.Metadata.RowGroupCount).IsEqualTo(1);
            await Assert.That(reader.Metadata.RowGroups[0].RowCount).IsEqualTo((ulong)(retained.Length + incoming.Length));
            await Assert.That(reader.Metadata.RowGroupSortingColumns(0).Length).IsEqualTo(expectedSorting ? 1 : 0);
            if (typeof(T) != typeof(string))
            {
                using var logicalReader = schema.CreateReader(new MemoryStream(File.ReadAllBytes(path)));
                var actual = new List<T>();
                foreach (var buffer in logicalReader.RowGroups[0].Column<T>(0))
                    actual.AddRange(buffer.Values);
                await Assert.That(actual.ToArray()).IsEquivalentTo(retained.Concat(incoming).ToArray());
            }
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    static string NewPath()
        => Path.Combine(Path.GetTempPath(), $"plank-decimal-append-{Guid.NewGuid():N}.parquet");
}
