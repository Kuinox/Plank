using Plank.Schema;
using Plank.Writing;
using PlankColumn = Plank.Schema.Column;

#pragma warning disable CA2007
namespace Plank.Tests;

internal sealed class OptionalAndCancellationTests
{
    [Test]
    public async Task CanceledTokenForRepeatedWriteReturnsCanceledTask()
    {
        using var stream = new MemoryStream();
        var schema = new ParquetSchema([
            new PlankColumn("A", ParquetPhysicalType.Int32, new ColumnOptions(ParquetRepetition.Repeated, []))
        ]);
        using var writer = ParquetWriter.Create(stream, schema);
        var rowGroup = writer.StartRowGroup();
        var token = new CancellationToken(canceled: true);

        await Assert.ThrowsAsync<TaskCanceledException>(async () =>
            await rowGroup.WriteAsync(schema.Columns[0], [[1, 2, 3]], token));
    }

    [Test]
    public async Task CanceledTokenForSerializedWriteReturnsCanceledTask()
    {
        using var stream = new MemoryStream();
        var schema = new ParquetSchema([
            new PlankColumn("A", ParquetPhysicalType.Int32, ColumnOptions.Default)
        ]);
        using var writer = ParquetWriter.Create(stream, schema);
        var serialized = writer.SerializeColumn(schema.Columns[0], new[] { 1, 2, 3 }, new byte[128]);
        var rowGroup = writer.StartRowGroup();
        var token = new CancellationToken(canceled: true);

        await Assert.ThrowsAsync<TaskCanceledException>(async () =>
            await rowGroup.WriteAsync(serialized, token));
    }

    [Test]
    public async Task OptionalByteArrayAllDefinedRoundTrips()
    {
        var path = Path.Combine(Path.GetTempPath(), $"plank-opt-bytes-all-defined-{Guid.NewGuid():N}.parquet");
        try
        {
            var schema = new ParquetSchema([
                new PlankColumn("A", ParquetPhysicalType.ByteArray, new ColumnOptions(ParquetRepetition.Optional, []))
            ]);
            byte[][] values =
            [
                [0x1, 0x2],
                [],
                [0xAA, 0xBB, 0xCC]
            ];

            await using (var fs = File.Create(path))
            using (var writer = ParquetWriter.Create(fs, schema))
            {
                var rowGroup = writer.StartRowGroup();
                await rowGroup.WriteAsync(schema.Columns[0], values);
                writer.CloseFile();
            }

            using var reader = new ParquetSharp.ParquetFileReader(path);
            using var rg = reader.RowGroup(0);
            using var col = rg.Column(0).LogicalReader<byte[]>();
            var read = col.ReadAll(values.Length);
            await Assert.That(read.Length).IsEqualTo(values.Length);
            for (var i = 0; i < values.Length; i++)
                await Assert.That(read[i].SequenceEqual(values[i])).IsTrue();
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
#pragma warning restore CA2007
