using System.Collections.Immutable;
using Plank.Schema;

namespace Plank.Tests;

internal sealed class SchemaTests
{
    static readonly string[] ExpectedColumns = ["A", "B"];

    [Test]
    public async Task SchemaStoresColumnsInOrder()
    {
        var schema = new ParquetSchema(ImmutableArray.Create(
            new Column("A", ParquetPhysicalType.Int32, ColumnOptions.Default),
            new Column("B", ParquetPhysicalType.Int32, ColumnOptions.Default)));
        schema.Validate();

        await Assert.That(schema.Columns.Select(c => c.Name).SequenceEqual(ExpectedColumns))
            .IsTrue();
    }

    [Test]
    public async Task SchemaRetainsColumnCount()
    {
        var schema = new ParquetSchema(ImmutableArray.Create(
            new Column("X", ParquetPhysicalType.Int32, ColumnOptions.Default),
            new Column("Y", ParquetPhysicalType.Int32, ColumnOptions.Default)));
        schema.Validate();

        await Assert.That(schema.Columns.Length)
            .IsEqualTo(2);
    }
}
