using Plank.Reading;
using Plank.Reading.Physical;
using Plank.Schema;
using Plank.Writing;

namespace Plank.Tests.Reading;

internal sealed class ColumnChunkMetadataTypeTests
{
    [Test]
    public void RejectsMistypedColumnChunkMetadata()
    {
        var schema = new ParquetSchema([ColumnDefinition.RequiredLeaf("value", ParquetPhysicalType.Int32)]);
        using var stream = new MemoryStream();
        using (var writer = schema.CreateWriter(stream, new ParquetWriterOptions { Compression = CompressionKind.None }))
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
        ReadOnlySpan<byte> pattern = [0x26, 0x00, 0x1c, 0x15, 0x02];
        var position = file.AsSpan(footerOffset).IndexOf(pattern);
        if (position < 0 || file.AsSpan(footerOffset + position + 1).IndexOf(pattern) >= 0)
            throw new InvalidOperationException("Expected a unique column chunk metadata sequence.");
        // Replace STRUCT with DOUBLE; leave the struct bytes in place.
        file[footerOffset + position + 2] = 0x17;
        using var reader = new ParquetFileReader();
        Assert.Throws<CorruptParquetException>(() => reader.Reset(new MemoryStream(file, writable: false)));
    }
}
