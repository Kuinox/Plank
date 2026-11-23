using Plank.Reading.Logical;
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

    [Test]
    public async Task ResetToAnotherStreamRejectsOpenRowGroup()
    {
        var schema = new ParquetSchema([
            ColumnDefinition.RequiredLeaf("first", ParquetPhysicalType.Int32),
            ColumnDefinition.RequiredLeaf("second", ParquetPhysicalType.Int32)
        ]);
        using var stream = new MemoryStream();
        using var other = new MemoryStream();
        using var writer = schema.CreateWriter(stream);
        var first = writer.CreateSerializedColumn<int>(schema.LeafColumns[0]);
        var second = writer.CreateSerializedColumn<int>(schema.LeafColumns[1]);
        var rowGroup = writer.StartRowGroup();
        first.Serialize([42]);
        rowGroup.Write(first);

        Assert.Throws<InvalidOperationException>(() => writer.Reset(other));

        second.Serialize([43]);
        rowGroup.Write(second);
        writer.CloseFile();
        using var reader = new ParquetReader();
        reader.Reset(new MemoryStream(stream.ToArray()));
        await Assert.That(reader.RowGroups.Count).IsEqualTo(1);
        await Assert.That(ReadColumn(reader.RowGroups[0].Column<int>(0))).IsEquivalentTo([42]);
        await Assert.That(ReadColumn(reader.RowGroups[0].Column<int>(1))).IsEquivalentTo([43]);
        await Assert.That(other.Length).IsEqualTo(0L);
    }

    static List<int> ReadColumn(RowGroupColumn<int> column)
    {
        var values = new List<int>();
        foreach (var buffer in column)
            values.AddRange(buffer.Values);
        return values;
    }
}
