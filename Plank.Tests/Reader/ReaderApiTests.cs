using Plank.Reading;
using Plank.Schema;
using Plank.Writing;
using PlankColumn = Plank.Schema.Column;

#pragma warning disable CA2007
namespace Plank.Tests;

sealed class ReaderApiTests
{
    [Test]
    public async Task ReadInt32RequiredRoundTrips()
    {
        using var stream = new MemoryStream();
        var schema = new ParquetSchema([
            new PlankColumn("A", ParquetPhysicalType.Int32, ColumnOptions.Default)
        ]);
        using (var writer = ParquetWriter.Create(stream, schema))
        {
            var rowGroup = writer.StartRowGroup();
            await rowGroup.WriteAsync(schema.Columns[0], [10, 20, 30]);
            writer.CloseFile();
        }

        var payload = stream.ToArray();
        using var reader = ParquetReader.Create(new MemoryStream(payload), schema);
        await Assert.That(reader.RowGroupCount).IsEqualTo(1);

        using var rowGroupReader = reader.StartRowGroup(0);
        var destination = new int[3];
        rowGroupReader.Read(schema.Columns[0], destination);
        await Assert.That(destination.SequenceEqual([10, 20, 30])).IsTrue();
    }

    [Test]
    public async Task ReadInt32OptionalRoundTrips()
    {
        using var stream = new MemoryStream();
        var schema = new ParquetSchema([
            new PlankColumn("A", ParquetPhysicalType.Int32, new ColumnOptions(ParquetRepetition.Optional, []))
        ]);
        using (var writer = ParquetWriter.Create(stream, schema))
        {
            var rowGroup = writer.StartRowGroup();
            await rowGroup.WriteAsync(schema.Columns[0], new int?[] { 7, null, 9 });
            writer.CloseFile();
        }

        var payload = stream.ToArray();
        using var reader = ParquetReader.Create(new MemoryStream(payload), schema);
        using var rowGroupReader = reader.StartRowGroup(0);
        var destination = new int?[3];
        rowGroupReader.ReadOptional(schema.Columns[0], destination);
        await Assert.That(destination.SequenceEqual(new int?[] { 7, null, 9 })).IsTrue();
    }

    [Test]
    public async Task StartRowGroupRejectsOutOfRange()
    {
        using var stream = new MemoryStream();
        var schema = new ParquetSchema([
            new PlankColumn("A", ParquetPhysicalType.Int32, ColumnOptions.Default)
        ]);
        using (var writer = ParquetWriter.Create(stream, schema))
        {
            var rowGroup = writer.StartRowGroup();
            await rowGroup.WriteAsync(schema.Columns[0], [1]);
            writer.CloseFile();
        }

        var payload = stream.ToArray();
        using var reader = ParquetReader.Create(new MemoryStream(payload), schema);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await Task.Run(() => reader.StartRowGroup(1)));
    }

    [Test]
    public async Task ReaderRejectsSchemaColumnCountMismatch()
    {
        using var stream = new MemoryStream();
        var writerSchema = new ParquetSchema([
            new PlankColumn("A", ParquetPhysicalType.Int32, ColumnOptions.Default)
        ]);
        using (var writer = ParquetWriter.Create(stream, writerSchema))
        {
            var rowGroup = writer.StartRowGroup();
            await rowGroup.WriteAsync(writerSchema.Columns[0], [1]);
            writer.CloseFile();
        }

        var payload = stream.ToArray();
        var readerSchema = new ParquetSchema([
            new PlankColumn("A", ParquetPhysicalType.Int32, ColumnOptions.Default),
            new PlankColumn("B", ParquetPhysicalType.Int32, ColumnOptions.Default)
        ]);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Task.Run(() => ParquetReader.Create(new MemoryStream(payload), readerSchema)));
    }

    [Test]
    public async Task ReadRejectsInvalidColumnContracts()
    {
        using var stream = new MemoryStream();
        var requiredSchema = new ParquetSchema([
            new PlankColumn("A", ParquetPhysicalType.Int32, ColumnOptions.Default)
        ]);
        using (var writer = ParquetWriter.Create(stream, requiredSchema))
        {
            var rowGroup = writer.StartRowGroup();
            await rowGroup.WriteAsync(requiredSchema.Columns[0], [1, 2]);
            writer.CloseFile();
        }

        var payload = stream.ToArray();
        using var reader = ParquetReader.Create(new MemoryStream(payload), requiredSchema);
        using var rowGroupReader = reader.StartRowGroup(0);
        var smallDestination = new int[1];
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await Task.Run(() => rowGroupReader.Read(requiredSchema.Columns[0], smallDestination)));

        var optionalSchema = new ParquetSchema([
            new PlankColumn("A", ParquetPhysicalType.Int32, new ColumnOptions(ParquetRepetition.Optional, []))
        ]);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Task.Run(() => rowGroupReader.Read(optionalSchema.Columns[0], new int[2])));
    }

    [Test]
    public async Task ReaderResetReusesInstanceForAnotherStream()
    {
        var schema = new ParquetSchema([
            new PlankColumn("A", ParquetPhysicalType.Int32, ColumnOptions.Default)
        ]);
        using var streamA = new MemoryStream();
        using (var writerA = ParquetWriter.Create(streamA, schema))
        {
            var rowGroup = writerA.StartRowGroup();
            await rowGroup.WriteAsync(schema.Columns[0], [1, 2, 3]);
            writerA.CloseFile();
        }
        var payloadA = streamA.ToArray();

        using var streamB = new MemoryStream();
        using (var writerB = ParquetWriter.Create(streamB, schema))
        {
            var rowGroup = writerB.StartRowGroup();
            await rowGroup.WriteAsync(schema.Columns[0], [4, 5]);
            writerB.CloseFile();
        }
        var payloadB = streamB.ToArray();

        using var reader = ParquetReader.Create(new MemoryStream(payloadA), schema);
        using (var rgA = reader.StartRowGroup(0))
        {
            var valuesA = new int[3];
            rgA.Read(schema.Columns[0], valuesA);
            await Assert.That(valuesA.SequenceEqual([1, 2, 3])).IsTrue();
        }

        reader.Reset(new MemoryStream(payloadB));
        using var rgB = reader.StartRowGroup(0);
        var valuesB = new int[2];
        rgB.Read(schema.Columns[0], valuesB);
        await Assert.That(valuesB.SequenceEqual([4, 5])).IsTrue();
    }
}
#pragma warning restore CA2007
