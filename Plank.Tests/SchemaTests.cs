using System.Collections.Immutable;
using Plank.Schema;

namespace Plank.Tests;

public sealed class SchemaTests
{
    [Test]
    public async Task Schema_StoresColumnsInOrder()
    {
        var schema = new ParquetSchema(ImmutableArray.Create(
            new Column("A", ParquetPhysicalType.Int32, ColumnOptions.Default),
            new Column("B", ParquetPhysicalType.Int32, ColumnOptions.Default)));
        schema.Validate();

        await Assert.That(schema.Columns.Select(c => c.Name).ToArray())
            .IsEqualTo(new[] { "A", "B" });
    }

    [Test]
    public async Task Schema_RetainsColumnCount()
    {
        var schema = new ParquetSchema(ImmutableArray.Create(
            new Column("X", ParquetPhysicalType.Int32, ColumnOptions.Default),
            new Column("Y", ParquetPhysicalType.Int32, ColumnOptions.Default)));
        schema.Validate();

        await Assert.That(schema.Columns.Length)
            .IsEqualTo(2);
    }
}
