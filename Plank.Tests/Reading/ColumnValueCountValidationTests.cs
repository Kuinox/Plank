using System.Collections.Immutable;
using Plank.Reading;
using Plank.Reading.Physical;
using Plank.Schema;
using Plank.Writing;

namespace Plank.Tests.Reading;

internal sealed class ColumnValueCountValidationTests
{
    [Test]
    public void RepeatedAncestorAllowsDifferentCountsAcrossRowGroups()
    {
        var schema = new ParquetSchema([
            ColumnDefinition.List("numbers", ColumnDefinition.RequiredLeaf("element", ParquetPhysicalType.Int32),
                repetition: ParquetRepetition.Required)
        ]);
        using var stream = new MemoryStream();
        using (var writer = schema.CreateWriter(stream, new ParquetWriterOptions { Compression = CompressionKind.None }))
        {
            for (var i = 0; i < 2; i++)
            {
                var rowGroup = writer.StartRowGroup();
                var column = rowGroup.CreateSerializedColumn<int[]>(schema.LeafColumns[0]);
                column.Serialize([new[] { 1, 2, 3 }, new[] { 4, 5 }]);
                rowGroup.Write(column);
            }
            writer.CloseFile();
        }

        using var reader = new ParquetFileReader();
        reader.Reset(new MemoryStream(stream.ToArray(), writable: false));
        if (reader.Metadata.RowGroupCount != 2)
            throw new InvalidOperationException("Expected two row groups.");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public void NonRepeatedColumnValueCountMustMatchRowCount(bool nested)
    {
        var leaf = ColumnDefinition.RequiredLeaf("value", ParquetPhysicalType.Int32,
            new ColumnOptions(encodings: ImmutableArray.Create(EncodingKind.Plain)));
        var schema = new ParquetSchema([nested ? ColumnDefinition.RequiredGroup("group", leaf) : leaf]);
        using var stream = new MemoryStream();
        using (var writer = schema.CreateWriter(stream, new ParquetWriterOptions
        {
            Compression = CompressionKind.None,
            WritePageIndexes = false
        }))
        {
            var column = writer.CreateSerializedColumn<int>(schema.LeafColumns[0]);
            column.Serialize([1, 2, 3, 4]);
            writer.StartRowGroup().Write(column);
            writer.CloseFile();
        }

        var file = stream.ToArray();
        using var probe = new ParquetFileReader();
        probe.Reset(new MemoryStream(file, writable: false));
        var footerOffset = checked((int)probe.Metadata.FooterOffset);
        // ColumnMetaData: codec=UNCOMPRESSED, num_values=4, then total_uncompressed_size.
        ReadOnlySpan<byte> pattern = [0x15, 0x00, 0x16, 0x08, 0x16];
        var position = file.AsSpan(footerOffset).IndexOf(pattern);
        if (position < 0 || file.AsSpan(footerOffset + position + 1).IndexOf(pattern) >= 0)
            throw new InvalidOperationException("Expected a unique column metadata sequence.");
        file[footerOffset + position + 3] = 0x00;
        using var reader = new ParquetFileReader();
        Assert.Throws<CorruptParquetException>(() => reader.Reset(new MemoryStream(file, writable: false)));
    }
}
