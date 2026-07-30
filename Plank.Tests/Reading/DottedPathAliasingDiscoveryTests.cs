using Plank.Reading.Logical;
using Plank.Schema;

namespace Plank.Tests.Reading;

internal sealed class DottedPathAliasingDiscoveryTests
{
    [Test]
    public void LiteralDottedSegmentDoesNotAliasNestedColumnPath()
    {
        var fileSchema = new ParquetSchema([
            ColumnDefinition.RequiredLeaf("a.b", ParquetPhysicalType.Int32),
            ColumnDefinition.RequiredGroup("a",
                ColumnDefinition.RequiredLeaf("b", ParquetPhysicalType.Int64))
        ]);
        using var stream = new MemoryStream();
        var writer = fileSchema.CreateWriter(stream);
        var writerRowGroup = writer.StartRowGroup();
        var dotted = writerRowGroup.CreateSerializedColumn<int>(fileSchema.LeafColumns[0]);
        dotted.Serialize([11, 12]);
        writerRowGroup.Write(dotted);
        var nested = writerRowGroup.CreateSerializedColumn<long>(fileSchema.LeafColumns[1]);
        nested.Serialize([101, 102]);
        writerRowGroup.Write(nested);
        writer.CloseFile();

        var requested = new ParquetSchema([
            ColumnDefinition.RequiredGroup("a",
                ColumnDefinition.RequiredLeaf("b", ParquetPhysicalType.Int64)),
            ColumnDefinition.RequiredLeaf("a.b", ParquetPhysicalType.Int32)
        ]);
        using var reader = requested.CreateReader(new MemoryStream(stream.ToArray()));

        if (reader.Schema.LeafColumns.Length != 2)
            throw new InvalidOperationException(
                $"Expected two distinct physical columns, got {reader.Schema.LeafColumns.Length}.");

        var rowGroup = reader.RowGroups[0];
        var nestedActual = ReadValues(rowGroup.Column<long>(requested.LeafColumns[0]));
        var dottedActual = ReadValues(rowGroup.Column<int>(requested.LeafColumns[1]));
        if (!nestedActual.AsSpan().SequenceEqual([101L, 102L]))
            throw new InvalidOperationException("The nested column was projected from the wrong physical ordinal.");
        if (!dottedActual.AsSpan().SequenceEqual([11, 12]))
            throw new InvalidOperationException("The dotted column was projected from the wrong physical ordinal.");
    }

    static T[] ReadValues<T>(RowGroupColumn<T> column)
    {
        var values = new List<T>();
        foreach (var buffer in column)
            foreach (var value in buffer.Values)
                values.Add(value);
        return values.ToArray();
    }
}
