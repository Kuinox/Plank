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
        var colA = Column<Row, int>.Create(
            "A",
            0,
            static (in Row row) => row.A,
            static (ref Row row, int value) => row.A = value);
        var colB = Column<Row, int>.Create(
            "B",
            1,
            static (in Row row) => row.B,
            static (ref Row row, int value) => row.B = value);

        var schema = ParquetSchema.Create(colA, colB);
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
