using ParquetSharp;
using Plank.Schema;
using Plank.Writing;
using PlankColumn = Plank.Schema.Column;

#pragma warning disable CA2007
namespace Plank.Tests;

internal sealed class DateTimePolicyCoverageTests
{
    static ParquetSchema Int64Schema()
        => new([
            new PlankColumn("A", ParquetPhysicalType.Int64, ColumnOptions.Default)
        ]);

    [Test]
    public async Task ConvertLocalToUtcOnlyConvertsLocalButRejectsUnspecified()
    {
        var schema = Int64Schema();
        using var stream = new MemoryStream();
        using var writer = ParquetWriter.Create(stream, schema, new ParquetWriterOptions
        {
            DateTimeKindHandling = DateTimeKindHandling.ConvertLocalToUtc
        });
        var rowGroup = writer.StartRowGroup();
        var local = new[] { new DateTime(2026, 2, 1, 10, 0, 0, DateTimeKind.Local) };
        var unspecified = new[] { new DateTime(2026, 2, 1, 10, 0, 0, DateTimeKind.Unspecified) };

        await rowGroup.WriteAsync(schema.Columns[0], local);
        var rg2 = writer.StartRowGroup();
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await rg2.WriteAsync(schema.Columns[0], unspecified));
    }

    [Test]
    public async Task AssumeUnspecifiedAsUtcAllowsUnspecifiedAndLocal()
    {
        var path = Path.Combine(Path.GetTempPath(), $"plank-datetime-assume-{Guid.NewGuid():N}.parquet");
        try
        {
            var schema = Int64Schema();
            var local = new DateTime(2026, 2, 1, 10, 0, 0, DateTimeKind.Local);
            var unspecified = new DateTime(2026, 2, 1, 11, 0, 0, DateTimeKind.Unspecified);
            var utc = new DateTime(2026, 2, 1, 12, 0, 0, DateTimeKind.Utc);
            await using (var stream = File.Create(path))
            using (var writer = ParquetWriter.Create(stream, schema, new ParquetWriterOptions
            {
                DateTimeKindHandling = DateTimeKindHandling.AssumeUnspecifiedAsUtc
            }))
            {
                var rowGroup = writer.StartRowGroup();
                await rowGroup.WriteAsync(schema.Columns[0], new[] { local, unspecified, utc });
                writer.CloseFile();
            }

            using var reader = new ParquetFileReader(path);
            using var rg = reader.RowGroup(0);
            using var col = rg.Column(0).LogicalReader<DateTime>();
            var read = col.ReadAll(3);
            await Assert.That(read[0].Ticks).IsEqualTo(local.ToUniversalTime().Ticks);
            await Assert.That(read[1].Ticks).IsEqualTo(DateTime.SpecifyKind(unspecified, DateTimeKind.Utc).Ticks);
            await Assert.That(read[2].Ticks).IsEqualTo(utc.Ticks);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Test]
    public async Task PreserveClockTimeWithAssumeFlagIsRejected()
    {
        var schema = Int64Schema();
        using var stream = new MemoryStream();
        using var writer = ParquetWriter.Create(stream, schema, new ParquetWriterOptions
        {
            DateTimeKindHandling = DateTimeKindHandling.PreserveClockTime | DateTimeKindHandling.AssumeUnspecifiedAsUtc
        });
        var rowGroup = writer.StartRowGroup();
        var values = new[] { new DateTime(2026, 2, 1, 10, 0, 0, DateTimeKind.Unspecified) };

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await rowGroup.WriteAsync(schema.Columns[0], values));
    }

    [Test]
    public async Task RepeatedDateTimePoliciesRejectInvalidKinds()
    {
        var schema = new ParquetSchema([
            new PlankColumn("A", ParquetPhysicalType.Int64, new ColumnOptions(ParquetRepetition.Repeated, []))
        ]);
        using var stream = new MemoryStream();
        using var writer = ParquetWriter.Create(stream, schema, new ParquetWriterOptions
        {
            DateTimeKindHandling = DateTimeKindHandling.RequireUtc
        });
        var rowGroup = writer.StartRowGroup();
        var rows = new[]
        {
            new[] { new DateTime(2026, 2, 1, 10, 0, 0, DateTimeKind.Local) }
        };

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await rowGroup.WriteAsync(schema.Columns[0], new RepeatedValues<DateTime>(rows)));
    }
}
#pragma warning restore CA2007
