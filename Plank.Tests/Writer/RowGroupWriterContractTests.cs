using System.Collections.Immutable;
using Plank.Schema;
using Plank.Writing;
using PlankColumn = Plank.Schema.Column;

namespace Plank.Tests.Writer;

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
        var writer = schema.CreateWriter(stream);
        var rowGroup = writer.StartRowGroup();
        var first = writer.CreateSerializedColumn<int>(schema.Columns[0]);
        var second = writer.CreateSerializedColumn<int>(schema.Columns[1]);

        second.Serialize([1, 2]);
        first.Serialize([3, 4]);

        Assert.ThrowsAsync<InvalidOperationException>(async () =>
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
        var writer = schema.CreateWriter(stream);
        var rowGroup = writer.StartRowGroup();
        var first = writer.CreateSerializedColumn<int>(schema.Columns[0]);
        var second = writer.CreateSerializedColumn<int>(schema.Columns[1]);

        first.Serialize([1, 2, 3]);
        second.Serialize([4, 5]);

        rowGroup.Write(first);
        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Task.Run(() => rowGroup.Write(second)).ConfigureAwait(false));
    }

    [Test]
    public async Task ThrowsWhenWritingAfterRowGroupIsComplete()
    {
        using var stream = new MemoryStream();
        var schema = new ParquetSchema([
            new PlankColumn("A", ParquetPhysicalType.Int32, ColumnOptions.Default)
        ]);
        var writer = schema.CreateWriter(stream);
        var rowGroup = writer.StartRowGroup();
        var col = writer.CreateSerializedColumn<int>(schema.Columns[0]);

        col.Serialize([1, 2, 3]);
        rowGroup.Write(col); // completes the row group

        col.Serialize([4, 5, 6]);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Task.Run(() => rowGroup.Write(col)).ConfigureAwait(false));
    }

    [Test]
    public async Task ThrowsWhenRleEncodingIsUsedForNonBooleanColumn()
    {
        Assert.ThrowsAsync<NotSupportedException>(async () =>
            await Task.Run(() => new PlankColumn("A", ParquetPhysicalType.Int32,
                new ColumnOptions(encodings: ImmutableArray.Create(EncodingKind.Rle)))).ConfigureAwait(false));
    }

    [Test]
    public async Task ThrowsWhenBitPackedEncodingIsUsedForDataColumn()
    {
        Assert.ThrowsAsync<NotSupportedException>(async () =>
            await Task.Run(() => new PlankColumn("A", ParquetPhysicalType.Int32,
                new ColumnOptions(encodings: ImmutableArray.Create(EncodingKind.BitPacked)))).ConfigureAwait(false));
    }

    [Test]
    public async Task WritesOptionalFlatStringColumnWithNulls()
    {
        using var stream = new NonClosingMemoryStream();
        var schema = new ParquetSchema([
            new PlankColumn("A", ParquetPhysicalType.ByteArray,
                new ColumnOptions(repetition: ParquetRepetition.Optional))
        ]);
        var writer = schema.CreateWriter(stream);
        var rowGroup = writer.StartRowGroup();
        var serialized = writer.CreateSerializedColumn<string>(schema.Columns[0]);

        serialized.Serialize(["a", null!, "bbb"]);
        rowGroup.Write(serialized);
        writer.CloseFile();

        Assert.That(stream.Length, Is.GreaterThan(0));
    }

    [Test]
    [Explicit]
    public void GenerateReadingFixtures()
    {
        var fixturesDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Reading", "Fixtures");

        // DictionaryLiteralRunBeforeRleRun: 8 distinct values (literal group) then 16 repeats (RLE run)
        // Schema selector byte 0x04 = schema 4 (int32 RleDictionary) prepended so the fixture matches
        // the fuzz-format used by ParquetReaderRobustnessTests.
        var schema = new ParquetSchema([
            new PlankColumn("val", ParquetPhysicalType.Int32,
                new ColumnOptions(encodings: ImmutableArray.Create(EncodingKind.RleDictionary)))
        ]);
        using var stream = new MemoryStream();
        var writer = schema.CreateWriter(stream);
        var rowGroup = writer.StartRowGroup();
        var col = writer.CreateSerializedColumn<int>(schema.Columns[0]);
        col.Serialize([1, 2, 3, 4, 5, 6, 7, 8, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1]);
        rowGroup.Write(col);
        writer.CloseFile();

        var parquet = stream.ToArray();
        var withSelector = new byte[1 + parquet.Length];
        withSelector[0] = 0x04; // schema 4 = int32 RleDictionary
        parquet.CopyTo(withSelector, 1);
        File.WriteAllBytes(Path.Combine(fixturesDir, "DictionaryLiteralRunBeforeRleRun.parquet"), withSelector);
    }

    sealed class NonClosingMemoryStream : MemoryStream
    {
        protected override void Dispose(bool disposing)
        {
        }
    }
}
