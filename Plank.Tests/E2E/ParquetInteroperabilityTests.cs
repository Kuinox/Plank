using ParquetSharp;
using Plank.Schema;
using Plank.Writing;
using PlankColumn = Plank.Schema.Column;

#pragma warning disable CA2007
namespace Plank.Tests;

internal sealed class ParquetInteroperabilityTests
{
    static readonly int?[] OptionalIntValues = [1, null, 3, null, 5, null, 7];
    static readonly string?[] OptionalStringValues = ["alpha", null, "", "omega", null];
    static readonly byte[][] ByteArrayValues =
    [
        [0x01, 0x02, 0x03],
        [],
        [0xFF],
        [0x10, 0x20]
    ];

    static readonly int[][] RepeatedIntValues =
    [
        [1, 2, 3],
        [],
        [4],
        [5, 6, 7, 8]
    ];

    static readonly int?[][] RepeatedNullableIntValues =
    [
        [1, null, 3],
        [],
        [null],
        [4, 5, null]
    ];

    static string NewTempPath(string suffix)
        => Path.Combine(Path.GetTempPath(), $"plank-interop-{suffix}-{Guid.NewGuid():N}.parquet");

    static void Cleanup(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    static RowGroupOptions SplitOptions()
        => new()
        {
            MaxCompressedBytes = 4 * 1024 * 1024,
            MaxPageValueCount = 16,
            MaxPageBytes = 128
        };

    [Test]
    public async Task OptionalIntColumnRoundTripsWithNulls()
    {
        var path = NewTempPath("optional-int");
        try
        {
            var schema = new ParquetSchema([
                new PlankColumn("A", ParquetPhysicalType.Int32, new ColumnOptions(ParquetRepetition.Optional, []))
            ]);
            await using (var stream = File.Create(path))
            using (var writer = ParquetWriter.Create(stream, schema))
            {
                var rowGroup = writer.StartRowGroup();
                await rowGroup.WriteAsync(schema.Columns[0], OptionalIntValues).ConfigureAwait(false);
                writer.CloseFile();
            }

            using var fileReader = new ParquetFileReader(path);
            using var rowGroupReader = fileReader.RowGroup(0);
            using var columnReader = rowGroupReader.Column(0).LogicalReader<int?>();
            var values = columnReader.ReadAll(OptionalIntValues.Length);

            await Assert.That(values.SequenceEqual(OptionalIntValues)).IsTrue();
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Test]
    public async Task OptionalStringColumnRoundTripsWithNulls()
    {
        var path = NewTempPath("optional-string");
        try
        {
            var schema = new ParquetSchema([
                new PlankColumn("A", ParquetPhysicalType.ByteArray, new ColumnOptions(ParquetRepetition.Optional, []))
            ]);
            await using (var stream = File.Create(path))
            using (var writer = ParquetWriter.Create(stream, schema))
            {
                var rowGroup = writer.StartRowGroup();
                await rowGroup.WriteAsync(schema.Columns[0], OptionalStringValues).ConfigureAwait(false);
                writer.CloseFile();
            }

            using var fileReader = new ParquetFileReader(path);
            using var rowGroupReader = fileReader.RowGroup(0);
            using var columnReader = rowGroupReader.Column(0).LogicalReader<string>();
            var values = columnReader.ReadAll(OptionalStringValues.Length);

            await Assert.That(values.Length).IsEqualTo(OptionalStringValues.Length);
            for (var i = 0; i < values.Length; i++)
                await Assert.That(values[i]).IsEqualTo(OptionalStringValues[i]);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Test]
    public async Task ByteArrayColumnRoundTrips()
    {
        var path = NewTempPath("byte-array");
        try
        {
            var schema = new ParquetSchema([
                new PlankColumn("A", ParquetPhysicalType.ByteArray, ColumnOptions.Default)
            ]);
            await using (var stream = File.Create(path))
            using (var writer = ParquetWriter.Create(stream, schema))
            {
                var rowGroup = writer.StartRowGroup();
                await rowGroup.WriteAsync(schema.Columns[0], ByteArrayValues).ConfigureAwait(false);
                writer.CloseFile();
            }

            using var fileReader = new ParquetFileReader(path);
            using var rowGroupReader = fileReader.RowGroup(0);
            using var columnReader = rowGroupReader.Column(0).LogicalReader<byte[]>();
            var values = columnReader.ReadAll(ByteArrayValues.Length);

            await Assert.That(values.Length).IsEqualTo(ByteArrayValues.Length);
            for (var i = 0; i < values.Length; i++)
                await Assert.That(values[i].SequenceEqual(ByteArrayValues[i])).IsTrue();
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Test]
    public async Task OptionalIntColumnWithSplittingAndZstdRoundTrips()
    {
        var path = NewTempPath("optional-int-split-zstd");
        try
        {
            var values = Enumerable.Range(0, 512).Select(i => i % 3 == 0 ? (int?)null : i).ToArray();
            var schema = new ParquetSchema([
                new PlankColumn("A", ParquetPhysicalType.Int32, new ColumnOptions(ParquetRepetition.Optional, []))
            ]);
            await using (var stream = File.Create(path))
            using (var writer = ParquetWriter.Create(stream, schema, new ParquetWriterOptions
            {
                Compression = CompressionKind.Zstd,
                RowGroupOptions = SplitOptions()
            }))
            {
                var rowGroup = writer.StartRowGroup();
                await rowGroup.WriteAsync(schema.Columns[0], values).ConfigureAwait(false);
                writer.CloseFile();
            }

            using var fileReader = new ParquetFileReader(path);
            using var rowGroupReader = fileReader.RowGroup(0);
            using var columnReader = rowGroupReader.Column(0).LogicalReader<int?>();
            var read = columnReader.ReadAll(values.Length);

            await Assert.That(read.SequenceEqual(values)).IsTrue();
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Test]
    public async Task RepeatedIntWithSplittingAndGzipRoundTrips()
    {
        var path = NewTempPath("repeated-int-split-gzip");
        try
        {
            var rows = Enumerable.Range(0, 128)
                .Select(i => Enumerable.Range(0, i % 7).Select(v => i * 10 + v).ToArray())
                .ToArray();
            var schema = new ParquetSchema([
                new PlankColumn("A", ParquetPhysicalType.Int32, new ColumnOptions(ParquetRepetition.Repeated, []))
            ]);
            await using (var stream = File.Create(path))
            using (var writer = ParquetWriter.Create(stream, schema, new ParquetWriterOptions
            {
                Compression = CompressionKind.Gzip,
                RowGroupOptions = SplitOptions()
            }))
            {
                var rowGroup = writer.StartRowGroup();
                await rowGroup.WriteAsync(schema.Columns[0], rows).ConfigureAwait(false);
                writer.CloseFile();
            }

            using var fileReader = new ParquetFileReader(path);
            using var rowGroupReader = fileReader.RowGroup(0);
            using var columnReader = rowGroupReader.Column(0).LogicalReader<int[]>();
            var values = columnReader.ReadAll(rows.Length);

            await Assert.That(values.Length).IsEqualTo(rows.Length);
            for (var i = 0; i < values.Length; i++)
                await Assert.That(values[i].SequenceEqual(rows[i])).IsTrue();
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Test]
    public async Task RepeatedNullableIntWithSplittingAndZstdRoundTrips()
    {
        var path = NewTempPath("repeated-nullable-int-split-zstd");
        try
        {
            var rows = Enumerable.Range(0, 96)
                .Select(i =>
                {
                    var width = i % 5;
                    var row = new int?[width];
                    for (var j = 0; j < width; j++)
                        row[j] = (i + j) % 4 == 0 ? null : i + j;
                    return row;
                })
                .ToArray();
            var schema = new ParquetSchema([
                new PlankColumn("A", ParquetPhysicalType.Int32, new ColumnOptions(ParquetRepetition.Repeated, []))
            ]);
            await using (var stream = File.Create(path))
            using (var writer = ParquetWriter.Create(stream, schema, new ParquetWriterOptions
            {
                Compression = CompressionKind.Zstd,
                RowGroupOptions = SplitOptions()
            }))
            {
                var rowGroup = writer.StartRowGroup();
                await rowGroup.WriteAsync(schema.Columns[0], rows).ConfigureAwait(false);
                writer.CloseFile();
            }

            using var fileReader = new ParquetFileReader(path);
            using var rowGroupReader = fileReader.RowGroup(0);
            using var columnReader = rowGroupReader.Column(0).LogicalReader<int?[]>();
            var values = columnReader.ReadAll(rows.Length);

            await Assert.That(values.Length).IsEqualTo(rows.Length);
            for (var i = 0; i < values.Length; i++)
                await Assert.That(values[i].SequenceEqual(rows[i])).IsTrue();
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Test]
    public async Task PreserializedRepeatedColumnsCanBeWrittenToLaterRowGroups()
    {
        var path = NewTempPath("preserialized-repeated");
        try
        {
            var rows1 = RepeatedIntValues;
            var rows2 = RepeatedNullableIntValues.Select(r => r.Select(v => v ?? -1).ToArray()).ToArray();
            var schema = new ParquetSchema([
                new PlankColumn("A", ParquetPhysicalType.Int32, new ColumnOptions(ParquetRepetition.Repeated, []))
            ]);
            var buffer1 = new byte[64 * 1024];
            var buffer2 = new byte[64 * 1024];

            await using (var stream = File.Create(path))
            using (var writer = ParquetWriter.Create(stream, schema, new ParquetWriterOptions
            {
                ExpectedRowGroupCount = 2,
                RowGroupRowCountHint = (uint)rows1.Length
            }))
            {
                var serialized1 = writer.SerializeColumn(schema.Columns[0], rows1, buffer1);
                var serialized2 = writer.SerializeColumn(schema.Columns[0], rows2, buffer2);

                var rg1 = writer.StartRowGroup();
                await rg1.WriteAsync(serialized1).ConfigureAwait(false);

                var rg2 = writer.StartRowGroup();
                await rg2.WriteAsync(serialized2).ConfigureAwait(false);

                writer.CloseFile();
            }

            using var fileReader = new ParquetFileReader(path);
            await Assert.That(fileReader.FileMetaData.NumRowGroups).IsEqualTo(2);
            using var rgReader1 = fileReader.RowGroup(0);
            using var rgReader2 = fileReader.RowGroup(1);
            using var colReader1 = rgReader1.Column(0).LogicalReader<int[]>();
            using var colReader2 = rgReader2.Column(0).LogicalReader<int[]>();
            var read1 = colReader1.ReadAll(rows1.Length);
            var read2 = colReader2.ReadAll(rows2.Length);

            for (var i = 0; i < read1.Length; i++)
                await Assert.That(read1[i].SequenceEqual(rows1[i])).IsTrue();
            for (var i = 0; i < read2.Length; i++)
                await Assert.That(read2[i].SequenceEqual(rows2[i])).IsTrue();
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Test]
    public async Task DateTimePreserveClockTimeWritesUnmodifiedTicks()
    {
        var path = NewTempPath("datetime-preserve-clock");
        try
        {
            var schema = new ParquetSchema([
                new PlankColumn("A", ParquetPhysicalType.Int64, ColumnOptions.Default)
            ]);
            var values = new[]
            {
                new DateTime(2026, 2, 1, 10, 0, 0, DateTimeKind.Local),
                new DateTime(2026, 2, 1, 11, 0, 0, DateTimeKind.Unspecified),
                new DateTime(2026, 2, 1, 12, 0, 0, DateTimeKind.Utc)
            };

            await using (var stream = File.Create(path))
            using (var writer = ParquetWriter.Create(stream, schema, new ParquetWriterOptions
            {
                DateTimeKindHandling = DateTimeKindHandling.PreserveClockTime
            }))
            {
                var rowGroup = writer.StartRowGroup();
                await rowGroup.WriteAsync(schema.Columns[0], values).ConfigureAwait(false);
                writer.CloseFile();
            }

            using var fileReader = new ParquetFileReader(path);
            using var rowGroupReader = fileReader.RowGroup(0);
            using var columnReader = rowGroupReader.Column(0).LogicalReader<DateTime>();
            var read = columnReader.ReadAll(values.Length);

            await Assert.That(read.Select(v => v.Ticks).SequenceEqual(values.Select(v => v.Ticks))).IsTrue();
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Test]
    public async Task RepeatedReferenceTypeInfersOptionalElementValues()
    {
        var path = NewTempPath("repeated-string-optional-element");
        try
        {
            var rows = new[]
            {
                new[] { "x", "y" },
                [],
                new string?[] { null, "z" }
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
            using var rowGroupReader = fileReader.RowGroup(0);
            using var columnReader = rowGroupReader.Column(0).LogicalReader<string?[]>();
            var read = columnReader.ReadAll(rows.Length);

            await Assert.That(read.Length).IsEqualTo(rows.Length);
            for (var i = 0; i < read.Length; i++)
            {
                await Assert.That(read[i].Length).IsEqualTo(rows[i].Length);
                for (var j = 0; j < read[i].Length; j++)
                    await Assert.That(read[i][j]).IsEqualTo(rows[i][j]);
            }
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Test]
    public async Task ByteArrayWithGzipCompressionRoundTrips()
    {
        var path = NewTempPath("byte-array-gzip");
        try
        {
            var values = Enumerable.Range(0, 128)
                .Select(i => Enumerable.Repeat((byte)(i % 251), (i % 17) + 1).ToArray())
                .ToArray();
            var schema = new ParquetSchema([
                new PlankColumn("A", ParquetPhysicalType.ByteArray, ColumnOptions.Default)
            ]);
            await using (var stream = File.Create(path))
            using (var writer = ParquetWriter.Create(stream, schema, new ParquetWriterOptions
            {
                Compression = CompressionKind.Gzip
            }))
            {
                var rowGroup = writer.StartRowGroup();
                await rowGroup.WriteAsync(schema.Columns[0], values).ConfigureAwait(false);
                writer.CloseFile();
            }

            using var fileReader = new ParquetFileReader(path);
            using var rowGroupReader = fileReader.RowGroup(0);
            using var columnReader = rowGroupReader.Column(0).LogicalReader<byte[]>();
            var read = columnReader.ReadAll(values.Length);

            await Assert.That(read.Length).IsEqualTo(values.Length);
            for (var i = 0; i < read.Length; i++)
                await Assert.That(read[i].SequenceEqual(values[i])).IsTrue();
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Test]
    public async Task TimeOnlyWithZstdCompressionRoundTrips()
    {
        var path = NewTempPath("timeonly-zstd");
        try
        {
            var values = Enumerable.Range(0, 512)
                .Select(i => new TimeOnly(i % 24, i % 60, i % 60, i % 1000))
                .ToArray();
            var schema = new ParquetSchema([
                new PlankColumn("A", ParquetPhysicalType.Int64, ColumnOptions.Default)
            ]);
            await using (var stream = File.Create(path))
            using (var writer = ParquetWriter.Create(stream, schema, new ParquetWriterOptions
            {
                Compression = CompressionKind.Zstd
            }))
            {
                var rowGroup = writer.StartRowGroup();
                await rowGroup.WriteAsync(schema.Columns[0], values).ConfigureAwait(false);
                writer.CloseFile();
            }

            using var fileReader = new ParquetFileReader(path);
            fileReader.LogicalTypeFactory.TimeAsTimeOnly = true;
            using var rowGroupReader = fileReader.RowGroup(0);
            using var columnReader = rowGroupReader.Column(0).LogicalReader<TimeOnly>();
            var read = columnReader.ReadAll(values.Length);

            await Assert.That(read.SequenceEqual(values)).IsTrue();
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Test]
    [Arguments(CompressionKind.Brotli)]
    [Arguments(CompressionKind.Gzip)]
    [Arguments(CompressionKind.Snappy)]
    [Arguments(CompressionKind.Lz4)]
    [Arguments(CompressionKind.Zstd)]
    public async Task Int32RoundTripsAcrossCompressionKinds(CompressionKind compression)
    {
        var path = NewTempPath($"compression-{compression}");
        try
        {
            var values = Enumerable.Range(0, 256).ToArray();
            var schema = new ParquetSchema([
                new PlankColumn("A", ParquetPhysicalType.Int32, ColumnOptions.Default)
            ]);
            await using (var stream = File.Create(path))
            using (var writer = ParquetWriter.Create(stream, schema, new ParquetWriterOptions
            {
                Compression = compression
            }))
            {
                var rowGroup = writer.StartRowGroup();
                await rowGroup.WriteAsync(schema.Columns[0], values).ConfigureAwait(false);
                writer.CloseFile();
            }

            using var fileReader = new ParquetFileReader(path);
            using var rowGroupReader = fileReader.RowGroup(0);
            using var columnReader = rowGroupReader.Column(0).LogicalReader<int>();
            var read = columnReader.ReadAll(values.Length);
            await Assert.That(read.SequenceEqual(values)).IsTrue();
        }
        finally
        {
            Cleanup(path);
        }
    }
}
#pragma warning restore CA2007
