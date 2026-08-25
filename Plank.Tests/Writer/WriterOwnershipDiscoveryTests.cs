using Plank.Schema;

namespace Plank.Tests.Writer;

internal sealed class WriterOwnershipDiscoveryTests
{
    [Test]
    public void RowGroupRejectsSerializedColumnOwnedByAnotherWriter()
    {
        var schema = new ParquetSchema([
            ColumnDefinition.RequiredLeaf("value", ParquetPhysicalType.Int32)
        ]);
        using var sourceStream = new MemoryStream();
        using var destinationStream = new MemoryStream();
        var sourceWriter = schema.CreateWriter(sourceStream);
        var destinationWriter = schema.CreateWriter(destinationStream);
        var foreignColumn = sourceWriter.CreateSerializedColumn<int>(schema.LeafColumns[0]);
        foreignColumn.Serialize([42]);
        var rowGroup = destinationWriter.StartRowGroup();

        Assert.Throws<InvalidOperationException>(() => rowGroup.Write(foreignColumn));
    }
}
