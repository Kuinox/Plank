namespace Plank.Tests;

public sealed class SchemaTests
{
    private sealed class Row
    {
        public int A { get; set; }
        public int B { get; set; }
    }

    [Test]
    public async Task For_ReturnsRegisteredSchema()
    {
        var schema = ParquetSchema.Define()
            .Column<int>("A")
            .Column<int>("B")
            .Build();
        ParquetSchema.Register<Row>(schema);

        var resolved = ParquetSchema.For<Row>(_ => { });

        await Assert.That(resolved.Columns.Select(c => c.Name).ToArray())
            .IsEqualTo(new[] { "A", "B" });
    }

    [Test]
    public async Task Define_AssignsOrdinalsInOrder()
    {
        var schema = ParquetSchema.Define()
            .Column<int>("X")
            .Column<int>("Y")
            .Build();

        await Assert.That(schema.Columns.Select(c => c.Ordinal).ToArray())
            .IsEqualTo(new[] { 0, 1 });
    }
}
