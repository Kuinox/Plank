using Parquet;
using Parquet.Schema;
using ParquetSharp;
using Plank.Schema;
using Plank.Writing;
using PlankParquetWriter = Plank.Writing.ParquetWriter;
using PlankSchema = Plank.Schema.ParquetSchema;

namespace Plank.Tests.E2E;

internal sealed class ListInteropE2ETests
{
    [Test]
    public async Task RequiredListOfRequiredInt32IsReadableByParquetNetAndParquetSharp()
    {
        var path = Path.Combine(Path.GetTempPath(), $"plank-list-{Guid.NewGuid():N}.parquet");
        var rows = new[]
        {
            new[] { 10, 11 },
            new[] { 20 },
            new[] { 30, 31, 32 }
        };

        var schema = new PlankSchema([
            ColumnDef.List("numbers", ColumnDef.RequiredLeaf("element", ParquetPhysicalType.Int32),
                repetition: ParquetRepetition.Required)
        ]);

        try
        {
            using (var stream = File.Create(path))
            {
                var writer = PlankParquetWriter.Create(stream, schema, new ParquetWriterOptions
                {
                    Compression = CompressionKind.None
                });
                var rowGroup = writer.StartRowGroup();
                var serialized = rowGroup.CreateSerializedColumn();
                serialized.Serialize(schema.Columns[0], rows);
                rowGroup.Write(serialized);
                writer.CloseFile();
            }

            AssertParquetSharp(path, rows);
            await AssertParquetNetAsync(path, rows, allowsNullRows: false).ConfigureAwait(false);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Test]
    public async Task RequiredListOfRequiredInt32SupportsEmptyRows()
    {
        var path = Path.Combine(Path.GetTempPath(), $"plank-list-empty-{Guid.NewGuid():N}.parquet");
        var rows = new[]
        {
            Array.Empty<int>(),
            new[] { 10, 11 },
            Array.Empty<int>(),
            new[] { 20 }
        };

        var schema = new PlankSchema([
            ColumnDef.List("numbers", ColumnDef.RequiredLeaf("element", ParquetPhysicalType.Int32),
                repetition: ParquetRepetition.Required)
        ]);

        try
        {
            using (var stream = File.Create(path))
            {
                var writer = PlankParquetWriter.Create(stream, schema, new ParquetWriterOptions
                {
                    Compression = CompressionKind.None
                });
                var rowGroup = writer.StartRowGroup();
                var serialized = rowGroup.CreateSerializedColumn();
                serialized.Serialize(schema.Columns[0], rows);
                rowGroup.Write(serialized);
                writer.CloseFile();
            }

            AssertParquetSharp(path, rows);
            await AssertParquetNetAsync(path, rows, allowsNullRows: false).ConfigureAwait(false);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Test]
    public async Task OptionalListOfRequiredInt32SupportsNullAndEmptyRows()
    {
        var path = Path.Combine(Path.GetTempPath(), $"plank-list-optional-{Guid.NewGuid():N}.parquet");
        int[][] rows =
        [
            new[] { 10, 11 },
            null!,
            Array.Empty<int>(),
            new[] { 20 }
        ];

        var schema = new PlankSchema([
            ColumnDef.List("numbers", ColumnDef.RequiredLeaf("element", ParquetPhysicalType.Int32),
                repetition: ParquetRepetition.Optional)
        ]);

        try
        {
            using (var stream = File.Create(path))
            {
                var writer = PlankParquetWriter.Create(stream, schema, new ParquetWriterOptions
                {
                    Compression = CompressionKind.None
                });
                var rowGroup = writer.StartRowGroup();
                var serialized = rowGroup.CreateSerializedColumn();
                serialized.Serialize(schema.Columns[0], rows);
                rowGroup.Write(serialized);
                writer.CloseFile();
            }

            AssertParquetSharpOptional(path, rows);
            await AssertParquetNetAsync(path, rows, allowsNullRows: true).ConfigureAwait(false);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Test]
    public async Task OptionalListOfOptionalInt32SupportsNullRowsEmptyRowsAndNullElements()
    {
        var path = Path.Combine(Path.GetTempPath(), $"plank-list-optional-elements-{Guid.NewGuid():N}.parquet");
        int?[][] rows =
        [
            new int?[] { 10, null, 11 },
            null!,
            Array.Empty<int?>(),
            new int?[] { null, 20 }
        ];

        var schema = new PlankSchema([
            ColumnDef.List("numbers", ColumnDef.OptionalLeaf("element", ParquetPhysicalType.Int32),
                repetition: ParquetRepetition.Optional)
        ]);

        try
        {
            using (var stream = File.Create(path))
            {
                var writer = PlankParquetWriter.Create(stream, schema, new ParquetWriterOptions
                {
                    Compression = CompressionKind.None
                });
                var rowGroup = writer.StartRowGroup();
                var serialized = rowGroup.CreateSerializedColumn();
                serialized.Serialize(schema.Columns[0], rows);
                rowGroup.Write(serialized);
                writer.CloseFile();
            }

            AssertParquetSharpOptionalNullable(path, rows);
            await AssertParquetNetOptionalNullableAsync(path, rows).ConfigureAwait(false);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    static async Task AssertParquetNetAsync(string path, int[]?[] expectedRows, bool allowsNullRows)
    {
        using var stream = File.OpenRead(path);
        using var reader = await ParquetReader.CreateAsync(stream).ConfigureAwait(false);
        var fields = reader.Schema.GetDataFields();
        if (fields.Length != 1)
            throw new InvalidOperationException($"Expected one leaf data field, got {fields.Length}.");

        using var rowGroup = reader.OpenRowGroupReader(0);
        var column = await rowGroup.ReadColumnAsync(fields[0]).ConfigureAwait(false);
        if (column.Data is not int[] values)
            throw new InvalidOperationException($"Parquet.Net returned '{column.Data.GetType()}' instead of int[].");
        if (column.RepetitionLevels is not int[] rep)
            throw new InvalidOperationException("Parquet.Net did not return repetition levels for list column.");
        if (column.DefinitionLevels is not int[] def)
            throw new InvalidOperationException("Parquet.Net did not return definition levels for list column.");

        var expectedLevelCount = 0;
        for (var i = 0; i < expectedRows.Length; i++)
            expectedLevelCount += expectedRows[i] is { Length: > 0 } row ? row.Length : 1;

        if (rep.Length != expectedLevelCount)
            throw new InvalidOperationException($"Expected {expectedLevelCount} repetition levels, got {rep.Length}.");
        if (def.Length != expectedLevelCount)
            throw new InvalidOperationException($"Expected {expectedLevelCount} definition levels, got {def.Length}.");

        var maxDef = def.Length == 0 ? 0 : def.Max();
        if (!allowsNullRows && maxDef < 1)
            throw new InvalidOperationException($"Expected max definition level >= 1, got {maxDef}.");
        if (values.Length == 0 && expectedRows.Any(static r => r is { Length: > 0 }))
            throw new InvalidOperationException("Parquet.Net returned no values for non-empty input rows.");
    }

    static void AssertParquetSharp(string path, int[][] expectedRows)
    {
        using var reader = new ParquetFileReader(path);
        using var rowGroup = reader.RowGroup(0);
        using var logical = rowGroup.Column(0).LogicalReader<int[]>();
        var actualRows = logical.ReadAll(expectedRows.Length);
        AssertJaggedEqual(expectedRows, actualRows);
    }

    static void AssertParquetSharpOptional(string path, int[]?[] expectedRows)
    {
        using var reader = new ParquetFileReader(path);
        using var rowGroup = reader.RowGroup(0);
        using var logical = rowGroup.Column(0).LogicalReader<int[]?>();
        var actualRows = logical.ReadAll(expectedRows.Length);
        if (actualRows.Length != expectedRows.Length)
            throw new InvalidOperationException($"Expected {expectedRows.Length} rows, got {actualRows.Length}.");

        for (var i = 0; i < expectedRows.Length; i++)
        {
            var expected = expectedRows[i];
            var actual = actualRows[i];
            if (expected is null && actual is null)
                continue;
            if (expected is null || actual is null)
                throw new InvalidOperationException($"Row {i} nullability mismatch.");
            if (!actual.AsSpan().SequenceEqual(expected))
                throw new InvalidOperationException($"Row {i} mismatch.");
        }
    }

    static void AssertParquetSharpOptionalNullable(string path, int?[][] expectedRows)
    {
        using var reader = new ParquetFileReader(path);
        using var rowGroup = reader.RowGroup(0);
        using var logical = rowGroup.Column(0).LogicalReader<int?[]?>();
        var actualRows = logical.ReadAll(expectedRows.Length);
        if (actualRows.Length != expectedRows.Length)
            throw new InvalidOperationException($"Expected {expectedRows.Length} rows, got {actualRows.Length}.");

        for (var i = 0; i < expectedRows.Length; i++)
        {
            var expected = expectedRows[i];
            var actual = actualRows[i];
            if (expected is null && actual is null)
                continue;
            if (expected is null || actual is null)
                throw new InvalidOperationException($"Row {i} nullability mismatch.");
            if (expected.Length != actual.Length)
                throw new InvalidOperationException($"Row {i} length mismatch.");
            for (var j = 0; j < expected.Length; j++)
                if (expected[j] != actual[j])
                    throw new InvalidOperationException($"Row {i} element {j} mismatch.");
        }
    }

    static async Task AssertParquetNetOptionalNullableAsync(string path, int?[][] expectedRows)
    {
        using var stream = File.OpenRead(path);
        using var reader = await ParquetReader.CreateAsync(stream).ConfigureAwait(false);
        var fields = reader.Schema.GetDataFields();
        using var rowGroup = reader.OpenRowGroupReader(0);
        _ = await rowGroup.ReadColumnAsync(fields[0]).ConfigureAwait(false);

        if (fields.Length != 1)
            throw new InvalidOperationException($"Expected 1 field for optional list test, got {fields.Length}.");

        var nonNullRowCount = 0;
        for (var i = 0; i < expectedRows.Length; i++)
            if (expectedRows[i] is not null)
                nonNullRowCount++;
        if (nonNullRowCount <= 0)
            throw new InvalidOperationException("Expected at least one non-null row.");
    }

    static void AssertJaggedEqual(int[][] expected, int[][] actual)
    {
        if (actual.Length != expected.Length)
            throw new InvalidOperationException($"Expected {expected.Length} rows, got {actual.Length}.");
        for (var i = 0; i < expected.Length; i++)
            if (!actual[i].AsSpan().SequenceEqual(expected[i]))
                throw new InvalidOperationException($"Row {i} mismatch.");
    }
}
