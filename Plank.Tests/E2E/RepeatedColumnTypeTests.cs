using ParquetSharp;
using Plank.Schema;
using Plank.Writing;
using PlankColumn = Plank.Schema.Column;

#pragma warning disable CA2007
namespace Plank.Tests;

internal sealed class RepeatedColumnTypeTests
{
    static string NewTempPath(string suffix)
        => Path.Combine(Path.GetTempPath(), $"plank-repeated-{suffix}-{Guid.NewGuid():N}.parquet");

    static void Cleanup(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    [Test]
    public async Task RepeatedBooleanRoundTrips()
    {
        var path = NewTempPath("bool");
        try
        {
            var rows = new[]
            {
                new[] { true, false, true },
                [],
                new[] { false }
            };
            var schema = new ParquetSchema([
                new PlankColumn("A", ParquetPhysicalType.Boolean, new ColumnOptions(ParquetRepetition.Repeated, []))
            ]);
            await using (var stream = File.Create(path))
            using (var writer = ParquetWriter.Create(stream, schema))
            {
                var rowGroup = writer.StartRowGroup();
                await rowGroup.WriteAsync(schema.Columns[0], rows).ConfigureAwait(false);
                writer.CloseFile();
            }

            using var fileReader = new ParquetFileReader(path);
            using var rg = fileReader.RowGroup(0);
            using var reader = rg.Column(0).LogicalReader<bool[]>();
            var values = reader.ReadAll(rows.Length);
            for (var i = 0; i < rows.Length; i++)
                await Assert.That(values[i].SequenceEqual(rows[i])).IsTrue();
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Test]
    public async Task RepeatedFloatRoundTrips()
    {
        var path = NewTempPath("float");
        try
        {
            var rows = new[]
            {
                new[] { 1.25f, -2.5f },
                [],
                new[] { 3.5f, 4.75f, 8.125f }
            };
            var schema = new ParquetSchema([
                new PlankColumn("A", ParquetPhysicalType.Float, new ColumnOptions(ParquetRepetition.Repeated, []))
            ]);
            await using (var stream = File.Create(path))
            using (var writer = ParquetWriter.Create(stream, schema))
            {
                var rowGroup = writer.StartRowGroup();
                await rowGroup.WriteAsync(schema.Columns[0], rows).ConfigureAwait(false);
                writer.CloseFile();
            }

            using var fileReader = new ParquetFileReader(path);
            using var rg = fileReader.RowGroup(0);
            using var reader = rg.Column(0).LogicalReader<float[]>();
            var values = reader.ReadAll(rows.Length);
            for (var i = 0; i < rows.Length; i++)
                await Assert.That(values[i].SequenceEqual(rows[i])).IsTrue();
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Test]
    public async Task RepeatedDoubleRoundTrips()
    {
        var path = NewTempPath("double");
        try
        {
            var rows = new[]
            {
                new[] { 1.25, -2.5 },
                [],
                new[] { 3.5, 4.75, 8.125 }
            };
            var schema = new ParquetSchema([
                new PlankColumn("A", ParquetPhysicalType.Double, new ColumnOptions(ParquetRepetition.Repeated, []))
            ]);
            await using (var stream = File.Create(path))
            using (var writer = ParquetWriter.Create(stream, schema))
            {
                var rowGroup = writer.StartRowGroup();
                await rowGroup.WriteAsync(schema.Columns[0], rows).ConfigureAwait(false);
                writer.CloseFile();
            }

            using var fileReader = new ParquetFileReader(path);
            using var rg = fileReader.RowGroup(0);
            using var reader = rg.Column(0).LogicalReader<double[]>();
            var values = reader.ReadAll(rows.Length);
            for (var i = 0; i < rows.Length; i++)
                await Assert.That(values[i].SequenceEqual(rows[i])).IsTrue();
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Test]
    public async Task RepeatedInt64RoundTrips()
    {
        var path = NewTempPath("int64");
        try
        {
            var rows = new[]
            {
                new[] { 1L, 2L, 3L },
                [],
                new[] { 9L }
            };
            var schema = new ParquetSchema([
                new PlankColumn("A", ParquetPhysicalType.Int64, new ColumnOptions(ParquetRepetition.Repeated, []))
            ]);
            await using (var stream = File.Create(path))
            using (var writer = ParquetWriter.Create(stream, schema))
            {
                var rowGroup = writer.StartRowGroup();
                await rowGroup.WriteAsync(schema.Columns[0], rows).ConfigureAwait(false);
                writer.CloseFile();
            }

            using var fileReader = new ParquetFileReader(path);
            using var rg = fileReader.RowGroup(0);
            using var reader = rg.Column(0).LogicalReader<long[]>();
            var values = reader.ReadAll(rows.Length);
            for (var i = 0; i < rows.Length; i++)
                await Assert.That(values[i].SequenceEqual(rows[i])).IsTrue();
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Test]
    public async Task RepeatedByteArrayRoundTrips()
    {
        var path = NewTempPath("bytes");
        try
        {
            var rows = new[]
            {
                new byte[][]
                {
                    [0x01, 0x02],
                    [0xAA]
                },
                [],
                new byte[][]
                {
                    []
                }
            };
            var schema = new ParquetSchema([
                new PlankColumn("A", ParquetPhysicalType.ByteArray, new ColumnOptions(ParquetRepetition.Repeated, []))
            ]);
            await using (var stream = File.Create(path))
            using (var writer = ParquetWriter.Create(stream, schema))
            {
                var rowGroup = writer.StartRowGroup();
                await rowGroup.WriteAsync(schema.Columns[0], rows).ConfigureAwait(false);
                writer.CloseFile();
            }

            using var fileReader = new ParquetFileReader(path);
            using var rg = fileReader.RowGroup(0);
            using var reader = rg.Column(0).LogicalReader<byte[][]>();
            var values = reader.ReadAll(rows.Length);
            for (var i = 0; i < rows.Length; i++)
            {
                await Assert.That(values[i].Length).IsEqualTo(rows[i].Length);
                for (var j = 0; j < rows[i].Length; j++)
                    await Assert.That(values[i][j].SequenceEqual(rows[i][j])).IsTrue();
            }
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Test]
    public async Task RepeatedDateOnlyAndTimeOnlyAreReadableByParquetSharp()
    {
        var datePath = NewTempPath("dateonly");
        var timePath = NewTempPath("timeonly");
        try
        {
            var dateRows = new[]
            {
                new[] { new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 2) },
                [],
                new[] { new DateOnly(2026, 2, 3) }
            };
            var timeRows = new[]
            {
                new[] { new TimeOnly(1, 2, 3), new TimeOnly(4, 5, 6) },
                [],
                new[] { new TimeOnly(23, 59, 59, 999) }
            };

            var dateSchema = new ParquetSchema([
                new PlankColumn("A", ParquetPhysicalType.Int32, new ColumnOptions(ParquetRepetition.Repeated, []))
            ]);
            await using (var stream = File.Create(datePath))
            using (var writer = ParquetWriter.Create(stream, dateSchema))
            {
                var rowGroup = writer.StartRowGroup();
                await rowGroup.WriteAsync(dateSchema.Columns[0], dateRows).ConfigureAwait(false);
                writer.CloseFile();
            }

            var timeSchema = new ParquetSchema([
                new PlankColumn("A", ParquetPhysicalType.Int64, new ColumnOptions(ParquetRepetition.Repeated, []))
            ]);
            await using (var stream = File.Create(timePath))
            using (var writer = ParquetWriter.Create(stream, timeSchema))
            {
                var rowGroup = writer.StartRowGroup();
                await rowGroup.WriteAsync(timeSchema.Columns[0], timeRows).ConfigureAwait(false);
                writer.CloseFile();
            }

            using (var dateReader = new ParquetFileReader(datePath))
            {
                dateReader.LogicalTypeFactory.DateAsDateOnly = true;
                using var rg = dateReader.RowGroup(0);
                using var logical = rg.Column(0).LogicalReader<DateOnly[]>();
                var values = logical.ReadAll(dateRows.Length);
                for (var i = 0; i < dateRows.Length; i++)
                    await Assert.That(values[i].SequenceEqual(dateRows[i])).IsTrue();
            }

            using (var timeReader = new ParquetFileReader(timePath))
            {
                timeReader.LogicalTypeFactory.TimeAsTimeOnly = true;
                using var rg = timeReader.RowGroup(0);
                using var logical = rg.Column(0).LogicalReader<TimeOnly[]>();
                var values = logical.ReadAll(timeRows.Length);
                for (var i = 0; i < timeRows.Length; i++)
                    await Assert.That(values[i].SequenceEqual(timeRows[i])).IsTrue();
            }
        }
        finally
        {
            Cleanup(datePath);
            Cleanup(timePath);
        }
    }

    [Test]
    public async Task RepeatedDateTimeAndDateTimeOffsetAreReadableByParquetSharp()
    {
        var dateTimePath = NewTempPath("datetime");
        var dtoPath = NewTempPath("dto");
        try
        {
            var dateTimeRows = new[]
            {
                new[]
                {
                    new DateTime(2026, 2, 1, 10, 0, 0, DateTimeKind.Utc),
                    new DateTime(2026, 2, 1, 10, 0, 1, DateTimeKind.Utc)
                },
                [],
                new[]
                {
                    new DateTime(2026, 2, 1, 10, 0, 2, DateTimeKind.Utc)
                }
            };
            var dtoRows = new[]
            {
                new[]
                {
                    new DateTimeOffset(2026, 2, 1, 10, 0, 0, TimeSpan.FromHours(2)),
                    new DateTimeOffset(2026, 2, 1, 8, 0, 1, TimeSpan.Zero)
                },
                [],
                new[]
                {
                    new DateTimeOffset(2026, 2, 1, 5, 0, 2, TimeSpan.FromHours(-3))
                }
            };

            var schema = new ParquetSchema([
                new PlankColumn("A", ParquetPhysicalType.Int64, new ColumnOptions(ParquetRepetition.Repeated, []))
            ]);

            await using (var stream = File.Create(dateTimePath))
            using (var writer = ParquetWriter.Create(stream, schema))
            {
                var rowGroup = writer.StartRowGroup();
                await rowGroup.WriteAsync(schema.Columns[0], dateTimeRows).ConfigureAwait(false);
                writer.CloseFile();
            }

            await using (var stream = File.Create(dtoPath))
            using (var writer = ParquetWriter.Create(stream, schema))
            {
                var rowGroup = writer.StartRowGroup();
                await rowGroup.WriteAsync(schema.Columns[0], dtoRows).ConfigureAwait(false);
                writer.CloseFile();
            }

            using (var dateTimeReader = new ParquetFileReader(dateTimePath))
            {
                using var rg = dateTimeReader.RowGroup(0);
                using var logical = rg.Column(0).LogicalReader<DateTime[]>();
                var values = logical.ReadAll(dateTimeRows.Length);
                for (var i = 0; i < values.Length; i++)
                    await Assert.That(values[i].Select(v => v.Ticks).SequenceEqual(dateTimeRows[i].Select(v => v.Ticks))).IsTrue();
            }

            using (var dtoReader = new ParquetFileReader(dtoPath))
            {
                using var rg = dtoReader.RowGroup(0);
                using var logical = rg.Column(0).LogicalReader<DateTime[]>();
                var values = logical.ReadAll(dtoRows.Length);
                for (var i = 0; i < values.Length; i++)
                    await Assert.That(values[i].Select(v => v.Ticks).SequenceEqual(dtoRows[i].Select(v => v.UtcTicks))).IsTrue();
            }
        }
        finally
        {
            Cleanup(dateTimePath);
            Cleanup(dtoPath);
        }
    }
}
#pragma warning restore CA2007
