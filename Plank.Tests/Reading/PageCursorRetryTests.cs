using Plank.Reading;
using Plank.Reading.Logical;
using Plank.Reading.Logical.Internal;
using Plank.Reading.Physical;
using Plank.Schema;
using Plank.Writing;

namespace Plank.Tests.Reading;

internal sealed class PageCursorRetryTests
{
    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public void FailedPayloadReadDoesNotSkipPage(bool pruned, bool dictionary)
    {
        var schema = new ParquetSchema([
            ColumnDefinition.RequiredLeaf("value", ParquetPhysicalType.Int32,
                new ColumnOptions(encodings: [dictionary ? EncodingKind.RleDictionary : EncodingKind.Plain]))
        ]);
        var values = dictionary ? Enumerable.Repeat(42, 100).ToArray() : [7, 42];
        using var output = new MemoryStream();
        using (var writer = schema.CreateWriter(output, new ParquetWriterOptions
               { WritePageIndexes = true, Compression = CompressionKind.None }))
        {
            var column = writer.CreateSerializedColumn<int>(schema.LeafColumns[0]);
            column.Serialize(values);
            writer.StartRowGroup().Write(column);
            writer.CloseFile();
        }

        using var source = new ShortReadSource(output.ToArray());
        using var reader = new ParquetFileReader();
        reader.Reset(source);
        using var index = PageMetadataReader.OpenHandle(reader, 0, 0, schema.LeafColumns[0], (ulong)values.Length);
        using var cursor = pruned
            ? reader.OpenPages(0, 0, index, static (in ParquetDataPageMetadata _) => true)
            : reader.OpenPages(0, 0);

        var chunk = reader.Metadata.ColumnChunk(0, 0);
        var pageStart = dictionary ? chunk.DictionaryPageOffset : chunk.DataPageOffset;
        using (var baseline = reader.OpenPages(0, 0))
        {
            if (!baseline.MoveNext() || (baseline.CurrentHeader.Type == PageHeaderType.DictionaryPage) != dictionary)
                throw new InvalidOperationException("Fixture does not have the expected first page type.");
            source.FaultOffset = pageStart + (uint)baseline.CurrentHeader.HeaderLength;
        }
        Assert.Throws<EndOfStreamException>(() => cursor.MoveNext());
        if (!cursor.MoveNext())
            throw new InvalidOperationException("The cursor skipped the failed page.");
        if (dictionary)
        {
            if (cursor.CurrentHeader.Type != PageHeaderType.DictionaryPage)
                throw new InvalidOperationException("The cursor skipped the failed dictionary page.");
            if (!cursor.MoveNext()) throw new InvalidOperationException("Expected a data page.");
        }
        if (cursor.CurrentPayload.IsEmpty || cursor.MoveNext())
            throw new InvalidOperationException("Expected exactly one nonempty data page.");
    }

    sealed class ShortReadSource(byte[] bytes) : IParquetReadSource
    {
        public ulong? FaultOffset { get; set; }
        public ulong Length => (ulong)bytes.Length;

        public void ReadExactly(ulong offset, Span<byte> destination)
        {
            if (FaultOffset == offset)
            {
                destination[0] = bytes[checked((int)offset)];
                FaultOffset = null;
                throw new EndOfStreamException("Injected partial page payload read.");
            }
            bytes.AsSpan(checked((int)offset), destination.Length).CopyTo(destination);
        }

        public void Dispose() { }
    }
}
