using Plank.Schema;
using Plank.Writing;
using PlankColumn = Plank.Schema.Column;

#pragma warning disable CA2007
namespace Plank.Tests;

internal sealed class SemanticConflictCoverageTests
{
    [Test]
    public async Task Int32ColumnRejectsMixingIntAndDateOnly()
    {
        using var stream = new MemoryStream();
        var schema = new ParquetSchema([
            new PlankColumn("A", ParquetPhysicalType.Int32, ColumnOptions.Default)
        ]);
        using var writer = ParquetWriter.Create(stream, schema, new ParquetWriterOptions
        {
            ExpectedRowGroupCount = 2
        });

        var rg1 = writer.StartRowGroup();
        await rg1.WriteAsync(schema.Columns[0], [1, 2, 3]);

        var rg2 = writer.StartRowGroup();
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await rg2.WriteAsync(schema.Columns[0], [new DateOnly(2026, 2, 1)]));
    }

    [Test]
    public async Task Int64ColumnRejectsMixingLongAndDateTime()
    {
        using var stream = new MemoryStream();
        var schema = new ParquetSchema([
            new PlankColumn("A", ParquetPhysicalType.Int64, ColumnOptions.Default)
        ]);
        using var writer = ParquetWriter.Create(stream, schema, new ParquetWriterOptions
        {
            ExpectedRowGroupCount = 2
        });

        var rg1 = writer.StartRowGroup();
        await rg1.WriteAsync(schema.Columns[0], [1L, 2L, 3L]);

        var rg2 = writer.StartRowGroup();
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await rg2.WriteAsync(schema.Columns[0], [new DateTime(2026, 2, 1, 10, 0, 0, DateTimeKind.Utc)]));
    }

    [Test]
    public async Task ByteArrayColumnRejectsMixingBytesAndString()
    {
        using var stream = new MemoryStream();
        var schema = new ParquetSchema([
            new PlankColumn("A", ParquetPhysicalType.ByteArray, ColumnOptions.Default)
        ]);
        using var writer = ParquetWriter.Create(stream, schema, new ParquetWriterOptions
        {
            ExpectedRowGroupCount = 2
        });

        var rg1 = writer.StartRowGroup();
        await rg1.WriteAsync(schema.Columns[0], new byte[][]
        {
            [0x1, 0x2],
            [0x3]
        });

        var rg2 = writer.StartRowGroup();
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await rg2.WriteAsync(schema.Columns[0], ["hello"]));
    }

    [Test]
    public async Task RepeatedColumnRejectsMixingRequiredAndOptionalElements()
    {
        using var stream = new MemoryStream();
        var schema = new ParquetSchema([
            new PlankColumn("A", ParquetPhysicalType.Int32, new ColumnOptions(ParquetRepetition.Repeated, []))
        ]);
        using var writer = ParquetWriter.Create(stream, schema, new ParquetWriterOptions
        {
            ExpectedRowGroupCount = 2
        });

        var rg1 = writer.StartRowGroup();
        await rg1.WriteAsync(schema.Columns[0], new RepeatedValues<int>([[1, 2], []]));

        var rg2 = writer.StartRowGroup();
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await rg2.WriteAsync(schema.Columns[0], new RepeatedValues<int?>([[1, null]])));
    }
}
#pragma warning restore CA2007
