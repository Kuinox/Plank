using ParquetSharp;
using Plank.Writing;

#pragma warning disable CA2007
namespace Plank.Tests;

sealed class RowSourceGenTypeHintTests
{
    [Test]
    public async Task TypeHintsEnableLogicalTypesAndNullableStrings()
    {
        var path = Path.Combine(Path.GetTempPath(), $"plank-rowgen-hints-{Guid.NewGuid():N}.parquet");
        try
        {
            await using (var stream = File.Create(path))
            using (var writer = ParquetWriter.Create(stream, GeneratedHintedSchemaHolder.Schema))
            {
                var rowGroup = writer.StartRowGroup();
                var rows = GeneratedHintedSchemaHolder_SchemaPlankRow.CreateWriter(rowGroup, 2);

                var row0 = rows.GetRow();
                row0.trip_date = new DateOnly(2026, 2, 1);
                row0.event_time = new DateTime(2026, 2, 1, 10, 0, 0, DateTimeKind.Utc);
                row0.event_time_opt = new DateTime(2026, 2, 1, 10, 0, 1, DateTimeKind.Utc);
                row0.event_offset = new DateTimeOffset(2026, 2, 1, 12, 0, 0, TimeSpan.FromHours(2));
                row0.event_clock = new TimeOnly(1, 2, 3, 4);
                row0.tag = "hello";
                row0.tag_opt = "x";
                rows.Next();

                var row1 = rows.GetRow();
                row1.trip_date = new DateOnly(2026, 2, 2);
                row1.event_time = new DateTime(2026, 2, 1, 10, 0, 2, DateTimeKind.Utc);
                row1.event_time_opt = null;
                row1.event_offset = new DateTimeOffset(2026, 2, 1, 8, 0, 3, TimeSpan.Zero);
                row1.event_clock = new TimeOnly(4, 5, 6, 7);
                row1.tag = "world";
                row1.tag_opt = null;
                rows.Next();

                await rows.WriteAsync();
                writer.CloseFile();
            }

            using var fileReader = new ParquetFileReader(path);
            fileReader.LogicalTypeFactory.DateAsDateOnly = true;
            fileReader.LogicalTypeFactory.TimeAsTimeOnly = true;
            using var rg = fileReader.RowGroup(0);

            using var dateReader = rg.Column(0).LogicalReader<DateOnly>();
            using var eventTimeReader = rg.Column(1).LogicalReader<DateTime>();
            using var eventTimeOptReader = rg.Column(2).LogicalReader<long?>();
            using var eventOffsetReader = rg.Column(3).LogicalReader<DateTime>();
            using var eventClockReader = rg.Column(4).LogicalReader<TimeOnly>();
            using var tagReader = rg.Column(5).LogicalReader<string>();
            using var tagOptReader = rg.Column(6).LogicalReader<string?>();

            var dates = dateReader.ReadAll(2);
            var eventTimes = eventTimeReader.ReadAll(2);
            var eventTimeOpts = eventTimeOptReader.ReadAll(2);
            var eventOffsets = eventOffsetReader.ReadAll(2);
            var eventClocks = eventClockReader.ReadAll(2);
            var tags = tagReader.ReadAll(2);
            var tagOpts = tagOptReader.ReadAll(2);

            await Assert.That(dates[0]).IsEqualTo(new DateOnly(2026, 2, 1));
            await Assert.That(dates[1]).IsEqualTo(new DateOnly(2026, 2, 2));
            await Assert.That(eventTimes[0].Ticks).IsEqualTo(new DateTime(2026, 2, 1, 10, 0, 0, DateTimeKind.Utc).Ticks);
            await Assert.That(eventTimeOpts[0]).IsNotNull();
            await Assert.That(eventTimeOpts[1]).IsNull();
            await Assert.That(eventOffsets[0].Ticks).IsEqualTo(new DateTimeOffset(2026, 2, 1, 12, 0, 0, TimeSpan.FromHours(2)).UtcTicks);
            await Assert.That(eventClocks[0]).IsEqualTo(new TimeOnly(1, 2, 3, 4));
            await Assert.That(tags[0]).IsEqualTo("hello");
            await Assert.That(tags[1]).IsEqualTo("world");
            await Assert.That(tagOpts[0]).IsEqualTo("x");
            await Assert.That(tagOpts[1]).IsNull();
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
