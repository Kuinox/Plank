using Plank.Schema;
using Plank.Writing;
using ParquetSharp;
using PlankColumn = Plank.Schema.Column;

#pragma warning disable CA2007
namespace Plank.Tests;

internal sealed class ColumnCodecTests
{
    [Test]
    public async Task NonPlainEncodingOnRequiredColumnThrows()
    {
        using var stream = new MemoryStream();
        var schema = new ParquetSchema([
            new PlankColumn("A", ParquetPhysicalType.Int32, new ColumnOptions(ParquetRepetition.Required, [EncodingKind.Rle]))
        ]);
        using var writer = ParquetWriter.Create(stream, schema);
        var rowGroup = writer.StartRowGroup();

        await Assert.ThrowsAsync<NotSupportedException>(async () =>
            await rowGroup.WriteAsync(schema.Columns[0], new[] { 1, 2, 3 }));
    }

    [Test]
    public async Task NonPlainEncodingOnRepeatedColumnThrows()
    {
        using var stream = new MemoryStream();
        var schema = new ParquetSchema([
            new PlankColumn("A", ParquetPhysicalType.Int32, new ColumnOptions(ParquetRepetition.Repeated, [EncodingKind.Rle]))
        ]);
        using var writer = ParquetWriter.Create(stream, schema);
        var rowGroup = writer.StartRowGroup();

        await Assert.ThrowsAsync<NotSupportedException>(async () =>
            await rowGroup.WriteAsync(schema.Columns[0], [[1, 2], [3]]));
    }

    [Test]
    public async Task EncodedPayloadExceedingInternalBufferForInt64Throws()
    {
        using var stream = new MemoryStream();
        var schema = new ParquetSchema([
            new PlankColumn("A", ParquetPhysicalType.Int64, ColumnOptions.Default)
        ]);
        using var writer = ParquetWriter.Create(stream, schema);
        var rowGroup = writer.StartRowGroup();
        var values = Enumerable.Range(0, 600_000).Select(static x => (long)x).ToArray();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await rowGroup.WriteAsync(schema.Columns[0], values));
    }

    [Test]
    public async Task EncodedPayloadExceedingInternalBufferForFloatThrows()
    {
        using var stream = new MemoryStream();
        var schema = new ParquetSchema([
            new PlankColumn("A", ParquetPhysicalType.Float, ColumnOptions.Default)
        ]);
        using var writer = ParquetWriter.Create(stream, schema);
        var rowGroup = writer.StartRowGroup();
        var values = Enumerable.Range(0, 1_100_000).Select(static x => x + 0.25f).ToArray();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await rowGroup.WriteAsync(schema.Columns[0], values));
    }

    [Test]
    public async Task EncodedPayloadExceedingInternalBufferForDoubleThrows()
    {
        using var stream = new MemoryStream();
        var schema = new ParquetSchema([
            new PlankColumn("A", ParquetPhysicalType.Double, ColumnOptions.Default)
        ]);
        using var writer = ParquetWriter.Create(stream, schema);
        var rowGroup = writer.StartRowGroup();
        var values = Enumerable.Range(0, 600_000).Select(static x => x + 0.5d).ToArray();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await rowGroup.WriteAsync(schema.Columns[0], values));
    }

    [Test]
    public async Task OptionalInt64AllDefinedIsWritable()
    {
        using var stream = new MemoryStream();
        var schema = new ParquetSchema([
            new PlankColumn("A", ParquetPhysicalType.Int64, new ColumnOptions(ParquetRepetition.Optional, [EncodingKind.Plain]))
        ]);
        using (var writer = ParquetWriter.Create(stream, schema, new ParquetWriterOptions
               {
                   RowGroupRowCountHint = 3
               }))
        {
            var rowGroup = writer.StartRowGroup();
            await rowGroup.WriteAsync(schema.Columns[0], new long[] { 10, 20, 30 });
            writer.CloseFile();
        }

        var payload = stream.ToArray();
        using var reader = new ParquetFileReader(new MemoryStream(payload));
        using var rg = reader.RowGroup(0);
        using var column = rg.Column(0).LogicalReader<long?>();
        var values = column.ReadAll(3);
        await Assert.That(values).IsEquivalentTo([10L as long?, 20L, 30L]);
    }

    [Test]
    public async Task OptionalDoubleAllDefinedIsWritable()
    {
        using var stream = new MemoryStream();
        var schema = new ParquetSchema([
            new PlankColumn("A", ParquetPhysicalType.Double, new ColumnOptions(ParquetRepetition.Optional, [EncodingKind.Plain]))
        ]);
        using (var writer = ParquetWriter.Create(stream, schema, new ParquetWriterOptions
               {
                   RowGroupRowCountHint = 3
               }))
        {
            var rowGroup = writer.StartRowGroup();
            await rowGroup.WriteAsync(schema.Columns[0], new[] { 1.5d, 2.5d, 3.5d });
            writer.CloseFile();
        }

        var payload = stream.ToArray();
        using var reader = new ParquetFileReader(new MemoryStream(payload));
        using var rg = reader.RowGroup(0);
        using var column = rg.Column(0).LogicalReader<double?>();
        var values = column.ReadAll(3);
        await Assert.That(values).IsEquivalentTo([1.5d as double?, 2.5d, 3.5d]);
    }

    [Test]
    public async Task OptionalDateTimeAllDefinedIsWritable()
    {
        using var stream = new MemoryStream();
        var schema = new ParquetSchema([
            new PlankColumn("A", ParquetPhysicalType.Int64, new ColumnOptions(ParquetRepetition.Optional, [EncodingKind.Plain]))
        ]);
        var values = new[]
        {
            new DateTime(2024, 1, 1, 1, 2, 3, DateTimeKind.Unspecified),
            new DateTime(2024, 1, 1, 1, 2, 4, DateTimeKind.Unspecified),
            new DateTime(2024, 1, 1, 1, 2, 5, DateTimeKind.Unspecified)
        };
        var expected = values.Select(static x => (DateTime?)x).ToArray();
        using (var writer = ParquetWriter.Create(stream, schema, new ParquetWriterOptions
               {
                   RowGroupRowCountHint = 3,
                   DateTimeKindHandling = DateTimeKindHandling.PreserveClockTime
               }))
        {
            var rowGroup = writer.StartRowGroup();
            await rowGroup.WriteAsync(schema.Columns[0], values);
            writer.CloseFile();
        }

        var payload = stream.ToArray();
        using var reader = new ParquetFileReader(new MemoryStream(payload));
        using var rg = reader.RowGroup(0);
        using var column = rg.Column(0).LogicalReader<DateTime?>();
        var read = column.ReadAll(3);
        await Assert.That(read).IsEquivalentTo(expected);
    }

    [Test]
    public async Task OptionalInt64WithNullsIsWritable()
    {
        using var stream = new MemoryStream();
        var schema = new ParquetSchema([
            new PlankColumn("A", ParquetPhysicalType.Int64, new ColumnOptions(ParquetRepetition.Optional, [EncodingKind.Plain]))
        ]);
        long?[] values = [10, null, 30];
        using (var writer = ParquetWriter.Create(stream, schema, new ParquetWriterOptions
               {
                   RowGroupRowCountHint = 3
               }))
        {
            var rowGroup = writer.StartRowGroup();
            await rowGroup.WriteAsync(schema.Columns[0], values);
            writer.CloseFile();
        }

        var payload = stream.ToArray();
        using var reader = new ParquetFileReader(new MemoryStream(payload));
        using var rg = reader.RowGroup(0);
        using var column = rg.Column(0).LogicalReader<long?>();
        var read = column.ReadAll(3);
        await Assert.That(read).IsEquivalentTo(values);
    }

    [Test]
    public async Task OptionalDoubleWithNullsIsWritable()
    {
        using var stream = new MemoryStream();
        var schema = new ParquetSchema([
            new PlankColumn("A", ParquetPhysicalType.Double, new ColumnOptions(ParquetRepetition.Optional, [EncodingKind.Plain]))
        ]);
        double?[] values = [1.5d, null, 3.5d];
        using (var writer = ParquetWriter.Create(stream, schema, new ParquetWriterOptions
               {
                   RowGroupRowCountHint = 3
               }))
        {
            var rowGroup = writer.StartRowGroup();
            await rowGroup.WriteAsync(schema.Columns[0], values);
            writer.CloseFile();
        }

        var payload = stream.ToArray();
        using var reader = new ParquetFileReader(new MemoryStream(payload));
        using var rg = reader.RowGroup(0);
        using var column = rg.Column(0).LogicalReader<double?>();
        var read = column.ReadAll(3);
        await Assert.That(read).IsEquivalentTo(values);
    }

    [Test]
    public async Task OptionalDateTimeWithNullsWritesNullablePayload()
    {
        using var stream = new MemoryStream();
        var schema = new ParquetSchema([
            new PlankColumn("A", ParquetPhysicalType.Int64, new ColumnOptions(ParquetRepetition.Optional, [EncodingKind.Plain]))
        ]);
        var d0 = new DateTime(2024, 1, 1, 1, 2, 3, DateTimeKind.Unspecified);
        var d2 = new DateTime(2024, 1, 1, 1, 2, 5, DateTimeKind.Unspecified);
        DateTime?[] values = [d0, null, d2];
        using (var writer = ParquetWriter.Create(stream, schema, new ParquetWriterOptions
               {
                   RowGroupRowCountHint = 3,
                   DateTimeKindHandling = DateTimeKindHandling.PreserveClockTime
               }))
        {
            var rowGroup = writer.StartRowGroup();
            await rowGroup.WriteAsync(schema.Columns[0], values);
            writer.CloseFile();
        }

        var payload = stream.ToArray();
        using var reader = new ParquetFileReader(new MemoryStream(payload));
        using var rg = reader.RowGroup(0);
        using var column = rg.Column(0).LogicalReader<long?>();
        var read = column.ReadAll(3);
        await Assert.That(read.Length).IsEqualTo(3);
        await Assert.That(read[0].HasValue).IsTrue();
        await Assert.That(read[1]).IsNull();
        await Assert.That(read[2].HasValue).IsTrue();
    }

    [Test]
    public async Task OptionalFloatIsNotSupportedYet()
    {
        using var stream = new MemoryStream();
        var schema = new ParquetSchema([
            new PlankColumn("A", ParquetPhysicalType.Float, new ColumnOptions(ParquetRepetition.Optional, [EncodingKind.Plain]))
        ]);
        using var writer = ParquetWriter.Create(stream, schema);
        var rowGroup = writer.StartRowGroup();
        float?[] values = [1.25f, null];

        await Assert.ThrowsAsync<NotSupportedException>(async () =>
            await rowGroup.WriteAsync(schema.Columns[0], values));
    }

    [Test]
    public async Task OptionalBooleanIsNotSupportedYet()
    {
        using var stream = new MemoryStream();
        var schema = new ParquetSchema([
            new PlankColumn("A", ParquetPhysicalType.Boolean, new ColumnOptions(ParquetRepetition.Optional, [EncodingKind.Plain]))
        ]);
        using var writer = ParquetWriter.Create(stream, schema);
        var rowGroup = writer.StartRowGroup();
        bool?[] values = [true, null];

        await Assert.ThrowsAsync<NotSupportedException>(async () =>
            await rowGroup.WriteAsync(schema.Columns[0], values));
    }
}
#pragma warning restore CA2007
