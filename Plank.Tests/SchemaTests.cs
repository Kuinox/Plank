using System.Collections.Immutable;
using Plank.Schema;

namespace Plank.Tests;

public sealed class SchemaTests
{
    sealed class Row
    {
        public int A { get; set; }
        public int B { get; set; }
    }

    [Test]
    public async Task For_ReturnsRegisteredSchema()
    {
        var schema = new ParquetSchema(ImmutableArray.Create(
            new Column("A", typeof(int), ColumnOptions.Default),
            new Column("B", typeof(int), ColumnOptions.Default)));
        ParquetSchema.Register<Row>(schema);

        var resolved = ParquetSchema.For<Row>(new RowSchema<Row>(Array.Empty<RowColumnDefinition<Row>>()));

        await Assert.That(resolved.Columns.Select(c => c.Name).ToArray())
            .IsEqualTo(new[] { "A", "B" });
    }

    [Test]
    public async Task Define_AssignsOrdinalsInOrder()
    {
        var schema = new ParquetSchema(ImmutableArray.Create(
            new Column("X", typeof(int), ColumnOptions.Default),
            new Column("Y", typeof(int), ColumnOptions.Default)));

        await Assert.That(schema.Columns.Select(c => c.Ordinal).ToArray())
            .IsEqualTo(new[] { 0, 1 });
    }
}
