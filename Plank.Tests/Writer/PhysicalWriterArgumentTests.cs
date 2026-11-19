using Plank.Schema;

namespace Plank.Tests.Writer;

internal sealed class PhysicalWriterArgumentTests
{
    [Test]
    public void CreateSerializedColumnRejectsNullLeafWithoutChangingWriterState()
    {
        var schema = new ParquetSchema([ColumnDefinition.RequiredLeaf("value", ParquetPhysicalType.Int32)]);
        using var output = new MemoryStream();
        using var writer = schema.CreateWriter(output);
        Assert.Throws<ArgumentNullException>(() => writer.CreateSerializedColumn<int>(null!));
        var group = writer.StartRowGroup();
        Assert.Throws<ArgumentNullException>(() => group.CreateSerializedColumn<int>(null!));

        var column = group.CreateSerializedColumn<int>(schema.LeafColumns[0]);
        column.Serialize([42]);
        group.Write(column);
        writer.CloseFile();
        using var reader = schema.CreateReader(new MemoryStream(output.ToArray()));
        if (reader.RowGroups[0].RowCount != 1)
            throw new InvalidOperationException("The writer did not recover from a rejected argument.");
    }
}
