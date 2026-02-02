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
        var schema = ParquetSchema.Create(
            ColumnDefinition.Create<int>("A"),
            ColumnDefinition.Create<int>("B"));
        ParquetSchema.Register<Row>(schema);

        var resolved = ParquetSchema.For<Row>(RowSchema<Row>.Create());

        await Assert.That(resolved.Columns.Select(c => c.Name).ToArray())
            .IsEqualTo(new[] { "A", "B" });
    }

    [Test]
    public async Task Define_AssignsOrdinalsInOrder()
    {
        var schema = ParquetSchema.Create(
            ColumnDefinition.Create<int>("X"),
            ColumnDefinition.Create<int>("Y"));

        await Assert.That(schema.Columns.Select(c => c.Ordinal).ToArray())
            .IsEqualTo(new[] { 0, 1 });
    }
}
