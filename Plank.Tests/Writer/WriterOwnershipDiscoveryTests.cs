using Plank.Schema;

namespace Plank.Tests.Writer;

internal sealed class WriterOwnershipDiscoveryTests
{
    [Test]
    public void DisposedWriterRejectsOperationsAndChildWrites()
    {
        var schema = new ParquetSchema([
            ColumnDefinition.RequiredLeaf("value", ParquetPhysicalType.Int32)
        ]);
        using var stream = new MemoryStream();
        using var resetStream = new MemoryStream();
        var writer = schema.CreateWriter(stream);
        var rowGroup = writer.StartRowGroup();
        var serialized = writer.CreateSerializedColumn<int>(schema.LeafColumns[0]);
        serialized.Serialize([42]);

        writer.Dispose();
        writer.Dispose();

        Assert.Throws<ObjectDisposedException>(() => _ = writer.RowApiMaxParallelism);
        Assert.Throws<ObjectDisposedException>(() => writer.CreateSerializedColumn<int>(schema.LeafColumns[0]));
        Assert.Throws<ObjectDisposedException>(() => writer.Reset(resetStream));
        Assert.Throws<ObjectDisposedException>(() => writer.StartRowGroup());
        Assert.Throws<ObjectDisposedException>(() => writer.CloseFile());
        Assert.Throws<ObjectDisposedException>(() => serialized.Serialize([43]));
        Assert.Throws<ObjectDisposedException>(() => rowGroup.Write(serialized));
    }

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
