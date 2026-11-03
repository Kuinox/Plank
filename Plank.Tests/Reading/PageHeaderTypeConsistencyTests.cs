using Plank.Reading;
using Plank.Reading.Physical;
using Plank.Schema;
using Plank.Writing;

namespace Plank.Tests.Reading;

internal sealed class PageHeaderTypeConsistencyTests
{
    [Test]
    public void RejectsMismatchedPageTypeAndHeader()
    {
        // A DATA_PAGE type with a DATA_PAGE_V2 header, as in the reported files.
        byte[] v2 = [0x15, 0x00, 0x15, 0x00, 0x15, 0x00, 0x5c,
            0x15, 0x0a, 0x15, 0x00, 0x15, 0x0a, 0x15, 0x00, 0x15, 0x00, 0x15, 0x00, 0x00, 0x00];
        Assert.Throws<CorruptParquetException>(() => PageHeaderReader.Read(v2));
    }

    [Test]
    public void RejectsDictionaryHeaderOnDataPage()
    {
        // A DATA_PAGE_V2 type with a dictionary header.
        byte[] dictionary = [0x15, 0x06, 0x15, 0x00, 0x15, 0x00, 0x4c,
            0x15, 0x06, 0x15, 0x00, 0x00, 0x00];
        Assert.Throws<CorruptParquetException>(() => PageHeaderReader.Read(dictionary));
    }

    [Test]
    public void PhysicalCursorRejectsMismatchedHeaderInFile()
    {
        var schema = new ParquetSchema([ColumnDefinition.Leaf("Value", ParquetPhysicalType.Int32)]);
        using var output = new MemoryStream();
        using (var writer = schema.CreateWriter(output, new ParquetWriterOptions { Compression = CompressionKind.None }))
        {
            var column = writer.CreateSerializedColumn<int>(schema.LeafColumns[0]);
            column.Serialize([1, 2, 3]);
            writer.StartRowGroup().Write(column);
            writer.CloseFile();
        }

        var file = output.ToArray();
        // Change DATA_PAGE_V2 to DATA_PAGE without changing the V2 header or payload.
        if (file[4] != 0x15 || file[5] != 0x06)
            throw new InvalidOperationException("Unexpected page header layout.");
        file[5] = 0x00;
        using var reader = new ParquetFileReader();
        reader.Reset(new MemoryStream(file));
        using var cursor = reader.OpenPages(0, 0);
        Assert.Throws<CorruptParquetException>(() => cursor.MoveNext());
    }
}
