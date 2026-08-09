using System.Collections.Immutable;
using Plank.Schema;
using Plank.Writing;

namespace Plank.Tests.Writer;

internal sealed class RowGroupWriterContractTests
{
    [Test]
    public async Task RejectsLeafColumnFromAnotherSchema()
    {
        using var stream = new MemoryStream();
        var schema = new ParquetSchema([
            ColumnDefinition.RequiredLeaf("A", ParquetPhysicalType.Int32)
        ]);
        var otherSchema = new ParquetSchema([
            ColumnDefinition.RequiredLeaf("A", ParquetPhysicalType.Int32)
        ]);
        var writer = schema.CreateWriter(stream);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await Task.Run(() => writer.CreateSerializedColumn<int>(otherSchema.LeafColumns[0])).ConfigureAwait(false));
    }

    [Test]
    public async Task ThrowsWhenColumnsAreWrittenOutOfOrder()
    {
        using var stream = new MemoryStream();
        var schema = new ParquetSchema([
            Plank.Schema.ColumnDefinition.Leaf("A", ParquetPhysicalType.Int32, ColumnOptions.Default),
            Plank.Schema.ColumnDefinition.Leaf("B", ParquetPhysicalType.Int32, ColumnOptions.Default)
        ]);
        var writer = schema.CreateWriter(stream);
        var rowGroup = writer.StartRowGroup();
        var first = writer.CreateSerializedColumn<int>(schema.LeafColumns[0]);
        var second = writer.CreateSerializedColumn<int>(schema.LeafColumns[1]);

        second.Serialize([1, 2]);
        first.Serialize([3, 4]);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Task.Run(() => rowGroup.Write(second)).ConfigureAwait(false));
    }

    [Test]
    public async Task ThrowsWhenRowCountsMismatchAcrossColumns()
    {
        using var stream = new MemoryStream();
        var schema = new ParquetSchema([
            Plank.Schema.ColumnDefinition.Leaf("A", ParquetPhysicalType.Int32, ColumnOptions.Default),
            Plank.Schema.ColumnDefinition.Leaf("B", ParquetPhysicalType.Int32, ColumnOptions.Default)
        ]);
        var writer = schema.CreateWriter(stream);
        var rowGroup = writer.StartRowGroup();
        var first = writer.CreateSerializedColumn<int>(schema.LeafColumns[0]);
        var second = writer.CreateSerializedColumn<int>(schema.LeafColumns[1]);

        first.Serialize([1, 2, 3]);
        second.Serialize([4, 5]);

        rowGroup.Write(first);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Task.Run(() => rowGroup.Write(second)).ConfigureAwait(false));
    }

    [Test]
    public async Task ThrowsWhenWritingAfterRowGroupIsComplete()
    {
        using var stream = new MemoryStream();
        var schema = new ParquetSchema([
            Plank.Schema.ColumnDefinition.Leaf("A", ParquetPhysicalType.Int32, ColumnOptions.Default)
        ]);
        var writer = schema.CreateWriter(stream);
        var rowGroup = writer.StartRowGroup();
        var col = writer.CreateSerializedColumn<int>(schema.LeafColumns[0]);

        col.Serialize([1, 2, 3]);
        rowGroup.Write(col); // completes the row group

        col.Serialize([4, 5, 6]);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Task.Run(() => rowGroup.Write(col)).ConfigureAwait(false));
    }

    [Test]
    public void SerializePreparesCompressionBeforeRowGroupWrite()
    {
        using var stream = new NonClosingMemoryStream();
        var schema = new ParquetSchema([
            ColumnDefinition.RequiredLeaf("A", ParquetPhysicalType.Int32,
                new ColumnOptions(encodings: [EncodingKind.Plain], compression: CompressionKind.Gzip,
                    compressionLevel: 1))
        ]);
        var writer = schema.CreateWriter(stream);
        var serialized = writer.CreateSerializedColumn<int>(schema.LeafColumns[0]);

        serialized.Serialize(new int[4096]);

        if (serialized.Pages.Count != 1)
            throw new InvalidOperationException($"Expected one prepared page, got {serialized.Pages.Count}.");
        ref var page = ref serialized.Pages[0];
        if (page.Header.WrittenLength == 0)
            throw new InvalidOperationException("Serialize did not prepare the page header.");
        if (page.Content.WrittenLength >= page.UncompressedContentSize)
            throw new InvalidOperationException("Serialize did not prepare the compressed page payload.");

        var preparedHeaderCrc = page.Header.ComputeCrc32();
        var preparedContentCrc = page.Content.ComputeCrc32();
        writer.StartRowGroup().Write(serialized);
        if (page.Header.ComputeCrc32() != preparedHeaderCrc || page.Content.ComputeCrc32() != preparedContentCrc)
            throw new InvalidOperationException("Writing changed a prepared page.");

        writer.CloseFile();
    }

    [Test]
    public async Task ThrowsWhenRleEncodingIsUsedForNonBooleanColumn()
    {
        await Assert.ThrowsAsync<NotSupportedException>(async () =>
            await Task.Run(() => Plank.Schema.ColumnDefinition.Leaf("A", ParquetPhysicalType.Int32,
                new ColumnOptions(encodings: ImmutableArray.Create(EncodingKind.Rle)))).ConfigureAwait(false));
    }

    [Test]
    public async Task ThrowsWhenBitPackedEncodingIsUsedForDataColumn()
    {
        await Assert.ThrowsAsync<NotSupportedException>(async () =>
            await Task.Run(() => Plank.Schema.ColumnDefinition.Leaf("A", ParquetPhysicalType.Int32,
                new ColumnOptions(encodings: ImmutableArray.Create(EncodingKind.BitPacked)))).ConfigureAwait(false));
    }

    [Test]
    public async Task WritesOptionalFlatBinaryColumnWithNulls()
    {
        using var stream = new NonClosingMemoryStream();
        var schema = new ParquetSchema([
            Plank.Schema.ColumnDefinition.Leaf("A", ParquetPhysicalType.ByteArray,
                new ColumnOptions(repetition: ParquetRepetition.Optional))
        ]);
        var writer = schema.CreateWriter(stream);
        var rowGroup = writer.StartRowGroup();
        var serialized = writer.CreateSerializedColumn<byte[]>(schema.LeafColumns[0]);

        serialized.Serialize(["a"u8.ToArray(), null!, "bbb"u8.ToArray()]);
        rowGroup.Write(serialized);
        writer.CloseFile();

        await Assert.That(stream.Length).IsGreaterThan(0);
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
            Plank.Schema.ColumnDefinition.Leaf("val", ParquetPhysicalType.Int32,
                new ColumnOptions(encodings: ImmutableArray.Create(EncodingKind.RleDictionary)))
        ]);
        using var stream = new MemoryStream();
        var writer = schema.CreateWriter(stream);
        var rowGroup = writer.StartRowGroup();
        var col = writer.CreateSerializedColumn<int>(schema.LeafColumns[0]);
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
