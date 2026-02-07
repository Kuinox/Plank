using Plank.Schema;

#pragma warning disable CA2007
namespace Plank.Tests;

internal sealed class SchemaTests
{
    static readonly string[] ExpectedColumns = ["A", "B"];

    [Test]
    public async Task SchemaStoresColumnsInOrder()
    {
        var schema = new ParquetSchema([
            new Column("A", ParquetPhysicalType.Int32, ColumnOptions.Default),
            new Column("B", ParquetPhysicalType.Int32, ColumnOptions.Default)
        ]);
        schema.Validate();

        await Assert.That(schema.Columns.Select(c => c.Name).SequenceEqual(ExpectedColumns))
            .IsTrue();
    }

    [Test]
    public async Task SchemaRetainsColumnCount()
    {
        var schema = new ParquetSchema([
            new Column("X", ParquetPhysicalType.Int32, ColumnOptions.Default),
            new Column("Y", ParquetPhysicalType.Int32, ColumnOptions.Default)
        ]);
        schema.Validate();

        await Assert.That(schema.Columns.Length)
            .IsEqualTo(2);
    }

    [Test]
    public async Task SchemaValidateHandlesDefaultAndEmptyColumns()
    {
        var defaultSchema = new ParquetSchema(default);
        defaultSchema.Validate();

        var emptySchema = new ParquetSchema([]);
        emptySchema.Validate();

        await Assert.That(defaultSchema.Columns.IsDefault).IsTrue();
        await Assert.That(emptySchema.Columns.Length).IsEqualTo(0);
    }

    [Test]
    public async Task SchemaValidateThrowsWhenColumnNameIsNull()
    {
        var schema = new ParquetSchema([
            new Column(null!, ParquetPhysicalType.Int32, ColumnOptions.Default)
        ]);

        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await Task.Run(() => schema.Validate()));
    }

    [Test]
    public async Task SchemaValidateThrowsWhenColumnNameIsWhitespace()
    {
        var schema = new ParquetSchema([
            new Column("   ", ParquetPhysicalType.Int32)
        ]);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await Task.Run(() => schema.Validate()));
    }

    [Test]
    public async Task SchemaValidateThrowsOnDuplicateColumnNames()
    {
        var schema = new ParquetSchema([
            new Column("A", ParquetPhysicalType.Int32),
            new Column("A", ParquetPhysicalType.Int64)
        ]);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Task.Run(() => schema.Validate()));
    }

    [Test]
    public async Task SchemaValidateThrowsOnInvalidPhysicalType()
    {
        var schema = new ParquetSchema([
            new Column("A", (ParquetPhysicalType)999)
        ]);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await Task.Run(() => schema.Validate()));
    }

    [Test]
    public async Task SchemaValidateThrowsOnInvalidRepetition()
    {
        var schema = new ParquetSchema([
            new Column("A", ParquetPhysicalType.Int32, new ColumnOptions((ParquetRepetition)999, []))
        ]);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await Task.Run(() => schema.Validate()));
    }

    [Test]
    public async Task SchemaValidateThrowsOnInvalidEncoding()
    {
        var schema = new ParquetSchema([
            new Column("A", ParquetPhysicalType.Int32, new ColumnOptions(ParquetRepetition.Required, [(EncodingKind)999]))
        ]);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await Task.Run(() => schema.Validate()));
    }

    [Test]
    public async Task SchemaValidateThrowsOnDuplicateEncoding()
    {
        var schema = new ParquetSchema([
            new Column("A", ParquetPhysicalType.Int32, new ColumnOptions(ParquetRepetition.Required, [EncodingKind.Plain, EncodingKind.Plain]))
        ]);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Task.Run(() => schema.Validate()));
    }

    [Test]
    public async Task SchemaValidateThrowsOnNullColumnEntry()
    {
        var schema = new ParquetSchema([
            null!
        ]);

        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await Task.Run(() => schema.Validate()));
    }

    [Test]
    public async Task ColumnCtorNullOptionsFallsBackToDefault()
    {
        var column = new Column("A", ParquetPhysicalType.Int32, null);

        await Assert.That(column.Options).IsEqualTo(ColumnOptions.Default);
    }

    [Test]
    public async Task ColumnOptionsCtorNormalizesDefaultEncodingsToEmpty()
    {
        var options = new ColumnOptions(ParquetRepetition.Required, default);

        await Assert.That(options.Encodings.IsDefault).IsFalse();
        await Assert.That(options.Encodings.Length).IsEqualTo(0);
        options.Validate();
    }

    [Test]
    public async Task ColumnOptionsValidateAllowsEmptyEncodingList()
    {
        var options = new ColumnOptions(ParquetRepetition.Optional, []);

        options.Validate();
        await Assert.That(options.Encodings.Length).IsEqualTo(0);
    }
}
#pragma warning restore CA2007
