using Parquet;
using ParquetSharp;
using Plank.Schema;
using Plank.Writing;
using PlankSchema = Plank.Schema.ParquetSchema;

namespace Plank.Tests.E2E;

internal sealed class NestedInteropE2ETests
{
    [Test]
    public async Task RequiredListOfRequiredGroupLeavesAreReadableByBothImplementations()
    {
        var path = Path.Combine(Path.GetTempPath(), $"plank-list-group-{Guid.NewGuid():N}.parquet");
        var aRows = new[]
        {
            new[] { 1, 2 },
            new[] { 3 }
        };
        var bRows = new[]
        {
            new[] { 10L, 20L },
            new[] { 30L }
        };

        var schema = new PlankSchema([
            ColumnDef.List("items",
                ColumnDef.RequiredGroup("entry",
                    ColumnDef.RequiredLeaf("a", ParquetPhysicalType.Int32),
                    ColumnDef.RequiredLeaf("b", ParquetPhysicalType.Int64)),
                repetition: ParquetRepetition.Required)
        ]);

        try
        {
            using (var stream = File.Create(path))
            {
                var writer = schema.CreateWriter(stream, new ParquetWriterOptions
                {
                    Compression = CompressionKind.None
                });
                var rowGroup = writer.StartRowGroup();

                var serializedA = rowGroup.CreateSerializedColumn<int[]>(schema.Columns[0]);
                serializedA.Serialize(aRows);
                rowGroup.Write(serializedA);

                var serializedB = rowGroup.CreateSerializedColumn<long[]>(schema.Columns[1]);
                serializedB.Serialize(bRows);
                rowGroup.Write(serializedB);

                writer.CloseFile();
            }

            AssertParquetSharpListGroup(path, aRows, bRows);
            await AssertParquetNetCanReadAsync(path, expectedLeafCount: 2).ConfigureAwait(false);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Test]
    public async Task RequiredMapOfInt32ToInt32LeavesAreReadableByBothImplementations()
    {
        var path = Path.Combine(Path.GetTempPath(), $"plank-map-{Guid.NewGuid():N}.parquet");
        var keyRows = new[]
        {
            new[] { 1, 2 },
            new[] { 3 }
        };
        var valueRows = new[]
        {
            new[] { 100, 200 },
            new[] { 300 }
        };

        var schema = new PlankSchema([
            ColumnDef.Map("scores",
                ColumnDef.RequiredLeaf("k", ParquetPhysicalType.Int32),
                ColumnDef.RequiredLeaf("v", ParquetPhysicalType.Int32),
                repetition: ParquetRepetition.Required)
        ]);

        try
        {
            using (var stream = File.Create(path))
            {
                var writer = schema.CreateWriter(stream, new ParquetWriterOptions
                {
                    Compression = CompressionKind.None
                });
                var rowGroup = writer.StartRowGroup();

                var serializedKeys = rowGroup.CreateSerializedColumn<int[]>(schema.Columns[0]);
                serializedKeys.Serialize(keyRows);
                rowGroup.Write(serializedKeys);

                var serializedValues = rowGroup.CreateSerializedColumn<int[]>(schema.Columns[1]);
                serializedValues.Serialize(valueRows);
                rowGroup.Write(serializedValues);

                writer.CloseFile();
            }

            AssertParquetSharpMapLeaves(path, keyRows, valueRows);
            await AssertParquetNetCanReadAsync(path, expectedLeafCount: 2).ConfigureAwait(false);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Test]
    public async Task RequiredMapOfInt32ToInt32LeavesWithSnappyIsReadableByBothImplementations()
    {
        var path = Path.Combine(Path.GetTempPath(), $"plank-map-snappy-{Guid.NewGuid():N}.parquet");
        var keyRows = new[]
        {
            new[] { 1, 2 },
            new[] { 3 }
        };
        var valueRows = new[]
        {
            new[] { 100, 200 },
            new[] { 300 }
        };

        var schema = new PlankSchema([
            ColumnDef.Map("scores",
                ColumnDef.RequiredLeaf("k", ParquetPhysicalType.Int32),
                ColumnDef.RequiredLeaf("v", ParquetPhysicalType.Int32),
                repetition: ParquetRepetition.Required)
        ]);

        try
        {
            using (var stream = File.Create(path))
            {
                var writer = schema.CreateWriter(stream, new ParquetWriterOptions
                {
                    Compression = CompressionKind.Snappy
                });
                var rowGroup = writer.StartRowGroup();

                var serializedKeys = rowGroup.CreateSerializedColumn<int[]>(schema.Columns[0]);
                serializedKeys.Serialize(keyRows);
                rowGroup.Write(serializedKeys);

                var serializedValues = rowGroup.CreateSerializedColumn<int[]>(schema.Columns[1]);
                serializedValues.Serialize(valueRows);
                rowGroup.Write(serializedValues);

                writer.CloseFile();
            }

            AssertParquetSharpMapLeaves(path, keyRows, valueRows);
            await AssertParquetNetCanReadAsync(path, expectedLeafCount: 2).ConfigureAwait(false);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Test]
    public async Task OptionalMapOfInt32ToOptionalInt32SupportsNullRowsEmptyRowsAndNullValues()
    {
        var path = Path.Combine(Path.GetTempPath(), $"plank-map-optional-{Guid.NewGuid():N}.parquet");
        int[][] keyRows =
        [
            new[] { 1, 2 },
            null!,
            Array.Empty<int>(),
            new[] { 3 }
        ];
        int?[][] valueRows =
        [
            new int?[] { 10, null },
            null!,
            Array.Empty<int?>(),
            new int?[] { 30 }
        ];

        var schema = new PlankSchema([
            ColumnDef.Map("scores",
                ColumnDef.RequiredLeaf("k", ParquetPhysicalType.Int32),
                ColumnDef.OptionalLeaf("v", ParquetPhysicalType.Int32),
                repetition: ParquetRepetition.Optional)
        ]);

        try
        {
            using (var stream = File.Create(path))
            {
                var writer = schema.CreateWriter(stream, new ParquetWriterOptions
                {
                    Compression = CompressionKind.Snappy
                });
                var rowGroup = writer.StartRowGroup();

                var serializedKeys = rowGroup.CreateSerializedColumn<int[]>(schema.Columns[0]);
                serializedKeys.Serialize(keyRows);
                rowGroup.Write(serializedKeys);

                var serializedValues = rowGroup.CreateSerializedColumn<int?[]>(schema.Columns[1]);
                serializedValues.Serialize(valueRows);
                rowGroup.Write(serializedValues);

                writer.CloseFile();
            }

            AssertParquetSharpOptionalMapLeaves(path, keyRows, valueRows);
            await AssertParquetNetCanReadAsync(path, expectedLeafCount: 2).ConfigureAwait(false);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Test]
    public async Task RequiredListOfRequiredListOfInt32SupportsEmptyInnerAndOuterLists()
    {
        var path = Path.Combine(Path.GetTempPath(), $"plank-list-list-{Guid.NewGuid():N}.parquet");
        int[][][] rows =
        [
            [new[] { 1, 2 }, Array.Empty<int>(), new[] { 3 }],
            [],
            [new[] { 4 }]
        ];

        var schema = new PlankSchema([
            ColumnDef.List("outer",
                ColumnDef.List("inner", ColumnDef.RequiredLeaf("value", ParquetPhysicalType.Int32),
                    repetition: ParquetRepetition.Required),
                repetition: ParquetRepetition.Required)
        ]);

        try
        {
            using (var stream = File.Create(path))
            {
                var writer = schema.CreateWriter(stream, new ParquetWriterOptions
                {
                    Compression = CompressionKind.Snappy
                });
                var rowGroup = writer.StartRowGroup();

                var serialized = rowGroup.CreateSerializedColumn<int[][]>(schema.Columns[0]);
                serialized.Serialize(rows);
                rowGroup.Write(serialized);

                writer.CloseFile();
            }

            AssertParquetSharpNestedList(path, rows);
            await AssertParquetNetNestedListAsync(path, rows).ConfigureAwait(false);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    static async Task AssertParquetNetCanReadAsync(string path, int expectedLeafCount)
    {
        using var stream = File.OpenRead(path);
        using var reader = await ParquetReader.CreateAsync(stream).ConfigureAwait(false);
        var fields = reader.Schema.GetDataFields();
        if (fields.Length != expectedLeafCount)
            throw new InvalidOperationException($"Expected {expectedLeafCount} data fields, got {fields.Length}.");
        using var rowGroup = reader.OpenRowGroupReader(0);
        for (var i = 0; i < fields.Length; i++)
            await rowGroup.ReadColumnAsync(fields[i]).ConfigureAwait(false);
    }

    static void AssertParquetSharpListGroup(string path, int[][] expectedA, long[][] expectedB)
    {
        using var reader = new ParquetFileReader(path);
        using var rowGroup = reader.RowGroup(0);

        using var aLogical = rowGroup.Column(0).LogicalReader<int[]>();
        var actualA = aLogical.ReadAll(expectedA.Length);
        AssertJaggedEqual(expectedA, actualA);

        using var bLogical = rowGroup.Column(1).LogicalReader<long[]>();
        var actualB = bLogical.ReadAll(expectedB.Length);
        AssertJaggedEqual(expectedB, actualB);
    }

    static void AssertParquetSharpMapLeaves(string path, int[][] expectedKeys, int[][] expectedValues)
    {
        using var reader = new ParquetFileReader(path);
        using var rowGroup = reader.RowGroup(0);

        using var keysLogical = rowGroup.Column(0).LogicalReader<int[]>();
        var actualKeys = keysLogical.ReadAll(expectedKeys.Length);
        AssertJaggedEqual(expectedKeys, actualKeys);

        using var valuesLogical = rowGroup.Column(1).LogicalReader<int[]>();
        var actualValues = valuesLogical.ReadAll(expectedValues.Length);
        AssertJaggedEqual(expectedValues, actualValues);
    }

    static void AssertParquetSharpOptionalMapLeaves(string path, int[][] expectedKeys, int?[][] expectedValues)
    {
        using var reader = new ParquetFileReader(path);
        using var rowGroup = reader.RowGroup(0);

        using var keysLogical = rowGroup.Column(0).LogicalReader<int[]?>();
        var actualKeys = keysLogical.ReadAll(expectedKeys.Length);
        if (actualKeys.Length != expectedKeys.Length)
            throw new InvalidOperationException($"Expected {expectedKeys.Length} key rows, got {actualKeys.Length}.");
        for (var i = 0; i < expectedKeys.Length; i++)
        {
            var expected = expectedKeys[i];
            var actual = actualKeys[i];
            if (expected is null && actual is null)
                continue;
            if (expected is null || actual is null)
                throw new InvalidOperationException($"Key row {i} nullability mismatch.");
            if (!actual.AsSpan().SequenceEqual(expected))
                throw new InvalidOperationException($"Key row {i} mismatch.");
        }

        using var valuesLogical = rowGroup.Column(1).LogicalReader<int?[]?>();
        var actualValues = valuesLogical.ReadAll(expectedValues.Length);
        if (actualValues.Length != expectedValues.Length)
            throw new InvalidOperationException($"Expected {expectedValues.Length} value rows, got {actualValues.Length}.");
        for (var i = 0; i < expectedValues.Length; i++)
        {
            var expected = expectedValues[i];
            var actual = actualValues[i];
            if (expected is null && actual is null)
                continue;
            if (expected is null || actual is null)
                throw new InvalidOperationException($"Value row {i} nullability mismatch.");
            if (expected.Length != actual.Length)
                throw new InvalidOperationException($"Value row {i} length mismatch.");
            for (var j = 0; j < expected.Length; j++)
                if (expected[j] != actual[j])
                    throw new InvalidOperationException($"Value row {i} element {j} mismatch.");
        }
    }

    static void AssertParquetSharpNestedList(string path, int[][][] expectedRows)
    {
        using var reader = new ParquetFileReader(path);
        using var rowGroup = reader.RowGroup(0);
        using var logical = rowGroup.Column(0).LogicalReader<int[][]>();
        var actualRows = logical.ReadAll(expectedRows.Length);
        if (actualRows.Length != expectedRows.Length)
            throw new InvalidOperationException($"Expected {expectedRows.Length} rows, got {actualRows.Length}.");

        for (var i = 0; i < expectedRows.Length; i++)
        {
            var expectedRow = expectedRows[i];
            var actualRow = actualRows[i];
            if (actualRow.Length != expectedRow.Length)
                throw new InvalidOperationException($"Row {i} inner list count mismatch.");
            for (var j = 0; j < expectedRow.Length; j++)
                if (!actualRow[j].AsSpan().SequenceEqual(expectedRow[j]))
                    throw new InvalidOperationException($"Row {i} inner list {j} mismatch.");
        }
    }

    static async Task AssertParquetNetNestedListAsync(string path, int[][][] expectedRows)
    {
        using var stream = File.OpenRead(path);
        using var reader = await ParquetReader.CreateAsync(stream).ConfigureAwait(false);
        var fields = reader.Schema.GetDataFields();
        if (fields.Length != 1)
            throw new InvalidOperationException($"Expected one nested list field, got {fields.Length}.");

        using var rowGroup = reader.OpenRowGroupReader(0);
        var column = await rowGroup.ReadColumnAsync(fields[0]).ConfigureAwait(false);
        if (column.Data.Length == 0)
            throw new InvalidOperationException("Nested list column returned no data.");
        if (column.RepetitionLevels is not int[] rep)
            throw new InvalidOperationException("Nested list column is missing repetition levels.");
        if (column.DefinitionLevels is not int[] def)
            throw new InvalidOperationException("Nested list column is missing definition levels.");
        if (rep.Length != def.Length)
            throw new InvalidOperationException("Nested list repetition/definition level lengths mismatch.");
        if (rep.Length < expectedRows.Length)
            throw new InvalidOperationException($"Expected at least {expectedRows.Length} level entries, got {rep.Length}.");
    }

    static void AssertJaggedEqual(int[][] expected, int[][] actual)
    {
        if (actual.Length != expected.Length)
            throw new InvalidOperationException($"Expected {expected.Length} rows, got {actual.Length}.");
        for (var i = 0; i < expected.Length; i++)
            if (!actual[i].AsSpan().SequenceEqual(expected[i]))
                throw new InvalidOperationException($"Row {i} mismatch.");
    }

    static void AssertJaggedEqual(long[][] expected, long[][] actual)
    {
        if (actual.Length != expected.Length)
            throw new InvalidOperationException($"Expected {expected.Length} rows, got {actual.Length}.");
        for (var i = 0; i < expected.Length; i++)
            if (!actual[i].AsSpan().SequenceEqual(expected[i]))
                throw new InvalidOperationException($"Row {i} mismatch.");
    }

    static void AssertJaggedEqual(int[][][] expected, int[][][] actual)
    {
        if (actual.Length != expected.Length)
            throw new InvalidOperationException($"Expected {expected.Length} rows, got {actual.Length}.");
        for (var i = 0; i < expected.Length; i++)
        {
            if (actual[i].Length != expected[i].Length)
                throw new InvalidOperationException($"Row {i} inner count mismatch.");
            for (var j = 0; j < expected[i].Length; j++)
                if (!actual[i][j].AsSpan().SequenceEqual(expected[i][j]))
                    throw new InvalidOperationException($"Row {i}, inner {j} mismatch.");
        }
    }
}
