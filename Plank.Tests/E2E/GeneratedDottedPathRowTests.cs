using Plank.Schema;

namespace Plank.Tests.E2E;

internal sealed class GeneratedDottedPathRowTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public void RowReaderKeepsDottedNamesDistinctFromNestedPaths(bool nestedFirst)
    {
        var dotted = ColumnDefinition.RequiredLeaf("a.b", ParquetPhysicalType.Int32);
        var nested = ColumnDefinition.RequiredGroup("a",
            ColumnDefinition.RequiredLeaf("b", ParquetPhysicalType.Int32));
        var schema = new ParquetSchema(nestedFirst ? [nested, dotted] : [dotted, nested]);
        using var output = new MemoryStream();
        using var writer = schema.CreateWriter(output);
        var rowGroup = writer.StartRowGroup();
        for (var ordinal = 0; ordinal < schema.LeafColumns.Length; ordinal++)
        {
            var column = rowGroup.CreateSerializedColumn<int>(schema.LeafColumns[ordinal]);
            var isNested = ordinal == (nestedFirst ? 0 : 1);
            column.Serialize(isNested ? [202, 203] : [101, 102]);
            rowGroup.Write(column);
        }
        writer.CloseFile();
        var bytes = output.ToArray();

        using var reader = GeneratedDottedPathSchema.CreateRowReader(new MemoryStream(bytes));
        for (var index = 0; index < 2; index++)
        {
            if (!reader.MoveNext())
                throw new InvalidOperationException("Expected another row.");
            var row = reader.Current;
            if (row.Literal != 101 + index || row.Nested.Value != 202 + index)
                throw new InvalidOperationException(
                    $"Expected distinct dotted and nested values, got {row.Literal} and {row.Nested.Value}.");
        }
        if (reader.MoveNext())
            throw new InvalidOperationException("Unexpected extra row.");

        reader.Reset(new MemoryStream(bytes), GeneratedDottedPathSchema.Projection.Nested);
        if (!reader.MoveNext() || reader.Current.Nested.Value != 202)
            throw new InvalidOperationException("The nested projection matched the literal dotted column.");

        reader.Reset(new MemoryStream(bytes), GeneratedDottedPathSchema.Projection.Literal);
        if (!reader.MoveNext() || reader.Current.Literal != 101)
            throw new InvalidOperationException("The literal dotted projection matched the nested column.");
    }
}

[ParquetSchema(AllowAllocatingValues = true)]
internal sealed partial class GeneratedDottedPathSchema
{
    [ParquetColumn("a.b")]
    public int Literal { get; init; }

    [ParquetColumn("a")]
    public GeneratedDottedPathChild Nested { get; init; } = new();
}

internal sealed class GeneratedDottedPathChild
{
    [ParquetColumn("b")]
    public int Value { get; init; }
}
