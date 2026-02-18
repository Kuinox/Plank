using Plank.Schema;
using Plank.Writing;
using PlankColumn = Plank.Schema.Column;
using System.Collections.Immutable;

namespace Plank.Tests;

internal sealed class RowGroupWriterContractTests
{
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

        second.Serialize(schema.Columns[1], [1, 2]);
        first.Serialize(schema.Columns[0], [3, 4]);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Task.Run(() => rowGroup.Write(second)).ConfigureAwait(false));
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

        first.Serialize(schema.Columns[0], [1, 2, 3]);
        second.Serialize(schema.Columns[1], [4, 5]);

        rowGroup.Write(first);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Task.Run(() => rowGroup.Write(second)).ConfigureAwait(false));
    }

    [Test]
    public async Task ThrowsWhenRleEncodingIsUsedForNonBooleanColumn()
    {
        using var stream = new MemoryStream();
        var schema = new ParquetSchema([
            new PlankColumn("A", ParquetPhysicalType.Int32,
                new ColumnOptions(encodings: ImmutableArray.Create(EncodingKind.Rle)))
        ]);
        var writer = ParquetWriter.Create(stream, schema);
        var serialized = writer.CreateSerializedColumn();

        await Assert.ThrowsAsync<NotSupportedException>(async () =>
            await Task.Run(() => serialized.Serialize(schema.Columns[0], [1, 2, 3])).ConfigureAwait(false));
    }

    [Test]
    public async Task ThrowsWhenBitPackedEncodingIsUsedForDataColumn()
    {
        using var stream = new MemoryStream();
        var schema = new ParquetSchema([
            new PlankColumn("A", ParquetPhysicalType.Int32,
                new ColumnOptions(encodings: ImmutableArray.Create(EncodingKind.BitPacked)))
        ]);
        var writer = ParquetWriter.Create(stream, schema);
        var serialized = writer.CreateSerializedColumn();

        await Assert.ThrowsAsync<NotSupportedException>(async () =>
            await Task.Run(() => serialized.Serialize(schema.Columns[0], [1, 2, 3])).ConfigureAwait(false));
    }
}
