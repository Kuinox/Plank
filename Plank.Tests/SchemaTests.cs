using System.Collections.Immutable;
using Plank.Schema;

namespace Plank.Tests;

public sealed class SchemaTests
{
    [Test]
    public async Task Schema_StoresColumnsInOrder()
    {
        var schema = new ParquetSchema(ImmutableArray.Create(
            new Column("A", typeof(int), ColumnOptions.Default),
            new Column("B", typeof(int), ColumnOptions.Default)));
        schema.Validate();

        await Assert.That(schema.Columns.Select(c => c.Name).ToArray())
            .IsEqualTo(new[] { "A", "B" });
    }

    [Test]
    public async Task Schema_RetainsColumnCount()
    {
        var schema = new ParquetSchema(ImmutableArray.Create(
            new Column("X", typeof(int), ColumnOptions.Default),
            new Column("Y", typeof(int), ColumnOptions.Default)));
        schema.Validate();

        await Assert.That(schema.Columns.Length)
            .IsEqualTo(2);
    }
}
