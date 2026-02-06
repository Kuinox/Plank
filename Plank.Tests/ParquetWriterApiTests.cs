using ParquetSharp;
using Plank.Schema;
using Plank.Writing;
using PlankColumn = Plank.Schema.Column;

#pragma warning disable CA2007
namespace Plank.Tests;

internal sealed class ParquetWriterApiTests
{
    static readonly int[] SampleInt32Values = [1, 2, 3, 4];
    static readonly long[] SampleInt64Values = [1L, 2L, 3L, 4L];
    static readonly DateTime[] SampleUtcDateTimeValues =
    [
        new DateTime(2026, 2, 1, 10, 0, 0, DateTimeKind.Utc),
        new DateTime(2026, 2, 1, 10, 0, 1, DateTimeKind.Utc)
    ];

    static string NewTempPath(string suffix)
        => Path.Combine(Path.GetTempPath(), $"plank-api-{suffix}-{Guid.NewGuid():N}.parquet");

    static void Cleanup(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    [Test]
    public async Task CloseFileWithoutRowGroupsProducesReadableEmptyFile()
    {
        var path = NewTempPath("empty");
        try
        {
            var schema = new ParquetSchema([
                new PlankColumn("A", ParquetPhysicalType.Int32, ColumnOptions.Default)
            ]);

            await using (var stream = File.Create(path))
            using (var writer = ParquetWriter.Create(stream, schema))
                writer.CloseFile();

            using var fileReader = new ParquetFileReader(path);
            await Assert.That(fileReader.FileMetaData.NumRowGroups).IsEqualTo(0);
            await Assert.That(fileReader.FileMetaData.NumRows).IsEqualTo(0L);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Test]
    public async Task CloseFileThrowsWhenRowGroupIsActive()
    {
        using var stream = new MemoryStream();
        var schema = new ParquetSchema([
            new PlankColumn("A", ParquetPhysicalType.Int32, ColumnOptions.Default)
        ]);
        using var writer = ParquetWriter.Create(stream, schema);

        _ = writer.StartRowGroup();
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Task.Run(() => writer.CloseFile()));
    }

    [Test]
    public async Task ResetThrowsWhenRowGroupIsActive()
    {
        using var stream = new MemoryStream();
        var schema = new ParquetSchema([
            new PlankColumn("A", ParquetPhysicalType.Int32, ColumnOptions.Default)
        ]);
        using var writer = ParquetWriter.Create(stream, schema);

        _ = writer.StartRowGroup();
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Task.Run(() => writer.Reset(new MemoryStream())));
    }

    [Test]
    public async Task StartRowGroupThrowsAfterCloseUntilReset()
    {
        using var stream = new MemoryStream();
        var schema = new ParquetSchema([
            new PlankColumn("A", ParquetPhysicalType.Int32, ColumnOptions.Default)
        ]);
        using var writer = ParquetWriter.Create(stream, schema);
        writer.CloseFile();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Task.Run(() => writer.StartRowGroup()));
    }

    [Test]
    public async Task StartRowGroupRejectsDifferentOptionsInstance()
    {
        using var stream = new MemoryStream();
        var schema = new ParquetSchema([
            new PlankColumn("A", ParquetPhysicalType.Int32, ColumnOptions.Default)
        ]);
        var writerOptions = new ParquetWriterOptions
        {
            RowGroupOptions = new RowGroupOptions
            {
                MaxEncodedBytes = 1024,
                MaxCompressedBytes = 1024
            }
        };
        using var writer = ParquetWriter.Create(stream, schema, writerOptions);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Task.Run(() => writer.StartRowGroup(new RowGroupOptions
            {
                MaxEncodedBytes = 1024,
                MaxCompressedBytes = 1024
            })));
    }

    [Test]
    public async Task WriteSameColumnTwiceThrows()
    {
        using var stream = new MemoryStream();
        var schema = new ParquetSchema([
            new PlankColumn("A", ParquetPhysicalType.Int32, ColumnOptions.Default)
        ]);
        using var writer = ParquetWriter.Create(stream, schema);
        var rowGroup = writer.StartRowGroup();

        await rowGroup.WriteAsync(schema.Columns[0], SampleInt32Values);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await rowGroup.WriteAsync(schema.Columns[0], SampleInt32Values));
    }

    [Test]
    public async Task WriteColumnFromAnotherSchemaThrows()
    {
        using var stream = new MemoryStream();
        var schemaA = new ParquetSchema([
            new PlankColumn("A", ParquetPhysicalType.Int32, ColumnOptions.Default)
        ]);
        var schemaB = new ParquetSchema([
            new PlankColumn("A", ParquetPhysicalType.Int32, ColumnOptions.Default)
        ]);
        using var writer = ParquetWriter.Create(stream, schemaA);
        var rowGroup = writer.StartRowGroup();

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await rowGroup.WriteAsync(schemaB.Columns[0], SampleInt32Values));
    }

    [Test]
    public async Task WriteWithWrongPhysicalTypeThrows()
    {
        using var stream = new MemoryStream();
        var schema = new ParquetSchema([
            new PlankColumn("A", ParquetPhysicalType.Int64, ColumnOptions.Default)
        ]);
        using var writer = ParquetWriter.Create(stream, schema);
        var rowGroup = writer.StartRowGroup();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await rowGroup.WriteAsync(schema.Columns[0], SampleInt32Values));
    }

    [Test]
    public async Task WriteScalarToRepeatedColumnThrows()
    {
        using var stream = new MemoryStream();
        var schema = new ParquetSchema([
            new PlankColumn("A", ParquetPhysicalType.Int32, new ColumnOptions(ParquetRepetition.Repeated, []))
        ]);
        using var writer = ParquetWriter.Create(stream, schema);
        var rowGroup = writer.StartRowGroup();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await rowGroup.WriteAsync(schema.Columns[0], SampleInt32Values));
    }

    [Test]
    public async Task WriteRepeatedToNonRepeatedColumnThrows()
    {
        using var stream = new MemoryStream();
        var schema = new ParquetSchema([
            new PlankColumn("A", ParquetPhysicalType.Int32, ColumnOptions.Default)
        ]);
        using var writer = ParquetWriter.Create(stream, schema);
        var rowGroup = writer.StartRowGroup();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await rowGroup.WriteAsync(schema.Columns[0], new RepeatedValues<int>([[1, 2]])));
    }

    [Test]
    public async Task RowCountMismatchAcrossColumnsThrows()
    {
        using var stream = new MemoryStream();
        var schema = new ParquetSchema([
            new PlankColumn("A", ParquetPhysicalType.Int32, ColumnOptions.Default),
            new PlankColumn("B", ParquetPhysicalType.Int32, ColumnOptions.Default)
        ]);
        using var writer = ParquetWriter.Create(stream, schema);
        var rowGroup = writer.StartRowGroup();

        await rowGroup.WriteAsync(schema.Columns[0], SampleInt32Values);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await rowGroup.WriteAsync(schema.Columns[1], [10, 20]));
    }

    [Test]
    public async Task SerializeColumnRejectsWrongPhysicalType()
    {
        using var stream = new MemoryStream();
        var schema = new ParquetSchema([
            new PlankColumn("A", ParquetPhysicalType.Int64, ColumnOptions.Default)
        ]);
        using var writer = ParquetWriter.Create(stream, schema);
        var destination = new byte[64];

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Task.Run(() => writer.SerializeColumn(schema.Columns[0], SampleInt32Values, destination)));
    }

    [Test]
    public async Task SerializeRepeatedColumnRejectsNonRepeatedColumn()
    {
        using var stream = new MemoryStream();
        var schema = new ParquetSchema([
            new PlankColumn("A", ParquetPhysicalType.Int32, ColumnOptions.Default)
        ]);
        using var writer = ParquetWriter.Create(stream, schema);
        var destination = new byte[1024];

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Task.Run(() => writer.SerializeRepeatedColumn(schema.Columns[0], new int[][] { [1, 2] }, destination)));
    }

    [Test]
    public async Task SerializeRepeatedColumnRejectsWrongPhysicalType()
    {
        using var stream = new MemoryStream();
        var schema = new ParquetSchema([
            new PlankColumn("A", ParquetPhysicalType.Int64, new ColumnOptions(ParquetRepetition.Repeated, []))
        ]);
        using var writer = ParquetWriter.Create(stream, schema);
        var destination = new byte[1024];

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Task.Run(() => writer.SerializeRepeatedColumn(schema.Columns[0], new int[][] { [1, 2] }, destination)));
    }

    [Test]
    public async Task LogicalSemanticMismatchAcrossRowGroupsThrows()
    {
        using var stream = new MemoryStream();
        var schema = new ParquetSchema([
            new PlankColumn("A", ParquetPhysicalType.Int64, ColumnOptions.Default)
        ]);
        using var writer = ParquetWriter.Create(stream, schema);

        var rg1 = writer.StartRowGroup();
        await rg1.WriteAsync(schema.Columns[0], SampleUtcDateTimeValues);

        var rg2 = writer.StartRowGroup();
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await rg2.WriteAsync(schema.Columns[0], SampleInt64Values));
    }

    [Test]
    public async Task ResetClearsLogicalSemanticState()
    {
        var schema = new ParquetSchema([
            new PlankColumn("A", ParquetPhysicalType.Int64, ColumnOptions.Default)
        ]);
        using var firstStream = new MemoryStream();
        using var writer = ParquetWriter.Create(firstStream, schema);

        var rg1 = writer.StartRowGroup();
        await rg1.WriteAsync(schema.Columns[0], SampleUtcDateTimeValues);
        writer.CloseFile();

        using var secondStream = new MemoryStream();
        writer.Reset(secondStream);
        var rg2 = writer.StartRowGroup();
        await rg2.WriteAsync(schema.Columns[0], SampleInt64Values);
        writer.CloseFile();
    }

    [Test]
    public async Task ConflictingDateTimeFlagsThrow()
    {
        using var stream = new MemoryStream();
        var schema = new ParquetSchema([
            new PlankColumn("A", ParquetPhysicalType.Int64, ColumnOptions.Default)
        ]);
        using var writer = ParquetWriter.Create(stream, schema, new ParquetWriterOptions
        {
            DateTimeKindHandling = DateTimeKindHandling.PreserveClockTime | DateTimeKindHandling.ConvertLocalToUtc
        });
        var rowGroup = writer.StartRowGroup();
        var values = new[] { new DateTime(2026, 2, 1, 10, 0, 0, DateTimeKind.Local) };

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await rowGroup.WriteAsync(schema.Columns[0], values));
    }

    [Test]
    public async Task PreCanceledWriteDoesNotPoisonRowGroup()
    {
        using var stream = new MemoryStream();
        var schema = new ParquetSchema([
            new PlankColumn("A", ParquetPhysicalType.Int32, ColumnOptions.Default)
        ]);
        using var writer = ParquetWriter.Create(stream, schema);
        var rowGroup = writer.StartRowGroup();
        var canceledToken = new CancellationToken(canceled: true);

        await Assert.ThrowsAsync<TaskCanceledException>(async () =>
            await rowGroup.WriteAsync(schema.Columns[0], SampleInt32Values, canceledToken));

        await rowGroup.WriteAsync(schema.Columns[0], SampleInt32Values);
        writer.CloseFile();
    }
}
#pragma warning restore CA2007
