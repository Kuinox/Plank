using Plank.Schema;
using Plank.Writing;
using PlankColumn = Plank.Schema.Column;

#pragma warning disable CA2007
namespace Plank.Tests;

internal sealed class WriterFailureTests
{
    [Test]
    public async Task CreateWithNullArgumentsThrows()
    {
        var schema = new ParquetSchema([
            new PlankColumn("A", ParquetPhysicalType.Int32, ColumnOptions.Default)
        ]);
        using var stream = new MemoryStream();

        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await Task.Run(() => ParquetWriter.Create(null!, schema)));
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await Task.Run(() => ParquetWriter.Create(stream, null!)));
    }

    [Test]
    public async Task InvalidWriterOptionsThrowAtCreate()
    {
        var schema = new ParquetSchema([
            new PlankColumn("A", ParquetPhysicalType.Int32, ColumnOptions.Default)
        ]);
        using var stream = new MemoryStream();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await Task.Run(() => ParquetWriter.Create(stream, schema, new ParquetWriterOptions
            {
                FooterBufferBytes = 0
            })));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await Task.Run(() => ParquetWriter.Create(stream, schema, new ParquetWriterOptions
            {
                ExpectedRowGroupCount = uint.MaxValue
            })));
    }

    [Test]
    public async Task SerializedColumnFromAnotherSchemaThrows()
    {
        using var streamA = new MemoryStream();
        using var streamB = new MemoryStream();
        var schemaA = new ParquetSchema([
            new PlankColumn("A", ParquetPhysicalType.Int32, ColumnOptions.Default)
        ]);
        var schemaB = new ParquetSchema([
            new PlankColumn("A", ParquetPhysicalType.Int32, ColumnOptions.Default)
        ]);
        using var writerA = ParquetWriter.Create(streamA, schemaA);
        using var writerB = ParquetWriter.Create(streamB, schemaB);

        var serialized = writerB.SerializeColumn(schemaB.Columns[0], new[] { 1, 2, 3 }, new byte[128]);
        var rowGroup = writerA.StartRowGroup();

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await rowGroup.WriteAsync(serialized));
    }

    [Test]
    public async Task SerializedColumnWrittenTwiceThrows()
    {
        using var stream = new MemoryStream();
        var schema = new ParquetSchema([
            new PlankColumn("A", ParquetPhysicalType.Int32, ColumnOptions.Default)
        ]);
        using var writer = ParquetWriter.Create(stream, schema);

        var serialized = writer.SerializeColumn(schema.Columns[0], new[] { 1, 2, 3 }, new byte[128]);
        var rowGroup = writer.StartRowGroup();
        await rowGroup.WriteAsync(serialized);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await rowGroup.WriteAsync(serialized));
    }

    [Test]
    public async Task SerializedRepeatedOptionalityConflictAcrossRowGroupsThrows()
    {
        using var stream = new MemoryStream();
        var schema = new ParquetSchema([
            new PlankColumn("A", ParquetPhysicalType.Int32, new ColumnOptions(ParquetRepetition.Repeated, []))
        ]);
        using var writer = ParquetWriter.Create(stream, schema, new ParquetWriterOptions
        {
            ExpectedRowGroupCount = 2
        });

        var requiredElements = writer.SerializeColumn(schema.Columns[0], new int[][] { [1, 2], [] }, new byte[256]);
        var optionalElements = writer.SerializeColumn(schema.Columns[0], new int?[][] { [1, null] }, new byte[256]);

        var rg1 = writer.StartRowGroup();
        await rg1.WriteAsync(requiredElements);

        var rg2 = writer.StartRowGroup();
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await rg2.WriteAsync(optionalElements));
    }

    [Test]
    public async Task ExceedingExpectedRowGroupCountThrows()
    {
        using var stream = new MemoryStream();
        var schema = new ParquetSchema([
            new PlankColumn("A", ParquetPhysicalType.Int32, ColumnOptions.Default)
        ]);
        using var writer = ParquetWriter.Create(stream, schema, new ParquetWriterOptions
        {
            ExpectedRowGroupCount = 1,
            RowGroupRowCountHint = 2
        });

        var first = writer.StartRowGroup();
        await first.WriteAsync(schema.Columns[0], new[] { 10, 20 });

        var second = writer.StartRowGroup();
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await second.WriteAsync(schema.Columns[0], new[] { 30, 40 }));
    }

    [Test]
    public async Task TinyFooterBufferThrows()
    {
        using var stream = new MemoryStream();
        var schema = new ParquetSchema([
            new PlankColumn("A", ParquetPhysicalType.Int32, ColumnOptions.Default)
        ]);
        using var writer = ParquetWriter.Create(stream, schema, new ParquetWriterOptions
        {
            FooterBufferBytes = 8
        });

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Task.Run(() => writer.CloseFile()));
    }

    [Test]
    public async Task CanceledCloseCanBeRetried()
    {
        using var stream = new MemoryStream();
        var schema = new ParquetSchema([
            new PlankColumn("A", ParquetPhysicalType.Int32, ColumnOptions.Default)
        ]);
        using var writer = ParquetWriter.Create(stream, schema);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await Task.Run(() => writer.CloseFile(cts.Token)));

        writer.CloseFile();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Task.Run(() => writer.StartRowGroup()));
    }

    [Test]
    public async Task ExpectedRowGroupCountZeroThrowsOnFirstCompletedRowGroup()
    {
        using var stream = new MemoryStream();
        var schema = new ParquetSchema([
            new PlankColumn("A", ParquetPhysicalType.Boolean, ColumnOptions.Default)
        ]);
        using var writer = ParquetWriter.Create(stream, schema, new ParquetWriterOptions
        {
            ExpectedRowGroupCount = 0
        });
        var rowGroup = writer.StartRowGroup();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await rowGroup.WriteAsync(schema.Columns[0], [true, false]));
    }

    [Test]
    public async Task SerializedBooleanAcrossRowGroupsDoesNotTriggerSemanticConflict()
    {
        using var stream = new MemoryStream();
        var schema = new ParquetSchema([
            new PlankColumn("A", ParquetPhysicalType.Boolean, ColumnOptions.Default)
        ]);
        using var writer = ParquetWriter.Create(stream, schema, new ParquetWriterOptions
        {
            ExpectedRowGroupCount = 2
        });

        var s1 = writer.SerializeColumn(schema.Columns[0], new[] { true, false }, new byte[64]);
        var s2 = writer.SerializeColumn(schema.Columns[0], new[] { false, true }, new byte[64]);
        var rg1 = writer.StartRowGroup();
        await rg1.WriteAsync(s1);
        var rg2 = writer.StartRowGroup();
        await rg2.WriteAsync(s2);
        writer.CloseFile();

        await Assert.That(stream.Length > 0).IsTrue();
    }

    [Test]
    public async Task CloseFileIsIdempotent()
    {
        using var stream = new MemoryStream();
        var schema = new ParquetSchema([
            new PlankColumn("A", ParquetPhysicalType.Int32, ColumnOptions.Default)
        ]);
        using var writer = ParquetWriter.Create(stream, schema);

        writer.CloseFile();
        await Task.Run(() => writer.CloseFile());
    }

    [Test]
    public async Task ResetWithNullStreamThrows()
    {
        using var stream = new MemoryStream();
        var schema = new ParquetSchema([
            new PlankColumn("A", ParquetPhysicalType.Int32, ColumnOptions.Default)
        ]);
        using var writer = ParquetWriter.Create(stream, schema);

        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await Task.Run(() => writer.Reset(null!)));
    }
}
#pragma warning restore CA2007
