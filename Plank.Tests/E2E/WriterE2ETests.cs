using Plank.Schema;
using Plank.Writing;
using PlankColumn = Plank.Schema.Column;

namespace Plank.Tests;

internal sealed class WriterE2ETests
{
    [Test]
    public async Task WritesSingleColumnRowGroup()
    {
        var path = Path.Combine(Path.GetTempPath(), $"plank-e2e-{Guid.NewGuid():N}.parquet");
        try
        {
            var schema = new ParquetSchema([
                new PlankColumn("A", ParquetPhysicalType.Int32, ColumnOptions.Default)
            ]);
            await using var stream = File.Create(path);
            var writer = ParquetWriter.Create(stream, schema);
            var rowGroup = writer.StartRowGroup();
            var serialized = writer.CreateSerializedColumn();

            serialized.Serialize(schema.Columns[0], new[] { 1, 2, 3, 4 });
            rowGroup.Write(serialized);
            writer.CloseFile();

            await Assert.That(new FileInfo(path).Length).IsGreaterThan(0L);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Test]
    public async Task ThrowsWhenColumnsAreWrittenOutOfOrder()
    {
        using var stream = new MemoryStream();
        var schema = new ParquetSchema([
            new PlankColumn("A", ParquetPhysicalType.Int32, ColumnOptions.Default),
            new PlankColumn("B", ParquetPhysicalType.Int32, ColumnOptions.Default)
        ]);
        var writer = ParquetWriter.Create(stream, schema);
        var rowGroup = writer.StartRowGroup();
        var first = writer.CreateSerializedColumn();
        var second = writer.CreateSerializedColumn();

        second.Serialize(schema.Columns[1], new[] { 1, 2 });
        first.Serialize(schema.Columns[0], new[] { 3, 4 });

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Task.Run(() => rowGroup.Write(second)));
    }

    [Test]
    public async Task ThrowsWhenRowCountsMismatchAcrossColumns()
    {
        using var stream = new MemoryStream();
        var schema = new ParquetSchema([
            new PlankColumn("A", ParquetPhysicalType.Int32, ColumnOptions.Default),
            new PlankColumn("B", ParquetPhysicalType.Int32, ColumnOptions.Default)
        ]);
        var writer = ParquetWriter.Create(stream, schema);
        var rowGroup = writer.StartRowGroup();
        var first = writer.CreateSerializedColumn();
        var second = writer.CreateSerializedColumn();

        first.Serialize(schema.Columns[0], new[] { 1, 2, 3 });
        second.Serialize(schema.Columns[1], new[] { 4, 5 });

        rowGroup.Write(first);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Task.Run(() => rowGroup.Write(second)));
    }

    [Test]
    public async Task SupportsConfiguredCompressionMode()
    {
        var path = Path.Combine(Path.GetTempPath(), $"plank-e2e-compression-{Guid.NewGuid():N}.parquet");
        try
        {
            var schema = new ParquetSchema([
                new PlankColumn("A", ParquetPhysicalType.Int32, ColumnOptions.Default)
            ]);
            await using var stream = File.Create(path);
            var writer = ParquetWriter.Create(stream, schema, new ParquetWriterOptions
            {
                Compression = CompressionKind.Snappy
            });
            var rowGroup = writer.StartRowGroup();
            var serialized = writer.CreateSerializedColumn();

            serialized.Serialize(schema.Columns[0], [10, 20, 30, 40]);
            rowGroup.Write(serialized);
            writer.CloseFile();

            await Assert.That(new FileInfo(path).Length).IsGreaterThan(0L);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
