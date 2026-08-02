using Plank.Schema;
using Plank.Untyped;
using Plank.Writing;

namespace Plank.Tests.E2E;

internal sealed class UntypedRowAdapterTests
{
    [Test]
    public void DictionaryRowsRoundTripFlatGroupsListsMapsAndNestedLists()
    {
        var schema = CreateSchema();
        IReadOnlyDictionary<string, object?>[] firstRows =
        [
            new Dictionary<string, object?>
            {
                ["id"] = 1,
                ["name"] = "one",
                ["profile"] = new Dictionary<string, object?>
                {
                    ["active"] = true,
                    ["born"] = new DateOnly(2020, 1, 2)
                },
                ["items"] = new object?[]
                {
                    new Dictionary<string, object?> { ["sku"] = 10, ["note"] = "ten" },
                    new Dictionary<string, object?> { ["sku"] = 20, ["note"] = "twenty" }
                },
                ["scores"] = new Dictionary<object, object?> { ["math"] = 100, ["art"] = null },
                ["nested"] = new object?[] { new object?[] { 1, 2 }, Array.Empty<object?>(), new object?[] { 3 } },
                ["optional_numbers"] = null,
                ["identifier"] = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff")
            },
            new Dictionary<string, object?>
            {
                ["id"] = 2,
                ["name"] = null,
                ["profile"] = new Dictionary<string, object?>
                {
                    ["active"] = false,
                    ["born"] = null
                },
                ["items"] = Array.Empty<object?>(),
                ["scores"] = new Dictionary<object, object?>(),
                ["nested"] = Array.Empty<object?>(),
                ["optional_numbers"] = Array.Empty<object?>(),
                ["identifier"] = Guid.Empty
            }
        ];
        IReadOnlyDictionary<string, object?>[] secondRows =
        [
            new Dictionary<string, object?>
            {
                ["id"] = 3L,
                ["name"] = "three",
                ["profile"] = new Dictionary<string, object?>
                {
                    ["active"] = false,
                    ["born"] = null
                },
                ["items"] = new object?[]
                {
                    new Dictionary<string, object?> { ["sku"] = 30, ["note"] = "thirty" }
                },
                ["scores"] = new Dictionary<object, object?> { ["science"] = 90 },
                ["nested"] = new object?[] { new object?[] { 4 } },
                ["optional_numbers"] = new object?[] { 7, null, 9 },
                ["identifier"] = Guid.Parse("fedcba98-7654-3210-fedc-ba9876543210")
            }
        ];

        var bytes = Write(schema, firstRows, secondRows);
        using var stream = new MemoryStream(bytes);
        using var reader = new ParquetUntypedReader();
        reader.Reset(stream);

        var all = reader.ReadAll();
        if (all.Count != 3)
            throw new InvalidOperationException($"Expected three rows, got {all.Count}.");
        AssertValue(firstRows[0], all[0], "row[0]");
        AssertValue(firstRows[1], all[1], "row[1]");
        AssertValue(secondRows[0], all[2], "row[2]");

        var secondGroup = reader.ReadRowGroup(1);
        if (secondGroup.Count != 1)
            throw new InvalidOperationException("Row-group projection returned the wrong row count.");
        AssertValue(secondRows[0], secondGroup[0], "rowGroup[1][0]");
    }

    [Test]
    public void ReaderDiscoversAndMaterializesParquetSharpSchema()
    {
        var path = Path.Combine(Path.GetTempPath(), $"plank-untyped-sharp-{Guid.NewGuid():N}.parquet");
        try
        {
            using (var writer = new ParquetSharp.ParquetFileWriter(path,
                       [new ParquetSharp.Column<int>("id"), new ParquetSharp.Column<string>("name")]))
            {
                using (var rowGroup = writer.AppendRowGroup())
                {
                    using (var ids = rowGroup.NextColumn().LogicalWriter<int>())
                        ids.WriteBatch([4, 5]);
                    using (var names = rowGroup.NextColumn().LogicalWriter<string>())
                        names.WriteBatch(["four", "five"]);
                }
                writer.Close();
            }

            using var stream = File.OpenRead(path);
            using var reader = new ParquetUntypedReader();
            reader.Reset(stream);
            var rows = reader.ReadAll();
            if (rows.Count != 2 || !Equals(rows[0]["id"], 4) || !Equals(rows[0]["name"], "four") ||
                !Equals(rows[1]["id"], 5) || !Equals(rows[1]["name"], "five"))
                throw new InvalidOperationException("ParquetSharp rows were not materialized correctly.");
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Test]
    public void WriterRejectsMissingRequiredValuesBeforeOpeningRowGroup()
    {
        var schema = new ParquetSchema([
            ColumnDefinition.RequiredLeaf("id", ParquetPhysicalType.Int32)
        ]);
        using var stream = new MemoryStream();
        var writer = new ParquetUntypedWriter(stream, schema);
        Expect<ArgumentException>(() => writer.WriteRowGroup([
            new Dictionary<string, object?>()
        ]));
        writer.WriteRowGroup([
            new Dictionary<string, object?> { ["id"] = 1 }
        ]);
        writer.CloseFile();
    }

    static ParquetSchema CreateSchema()
        => new([
            ColumnDefinition.RequiredLeaf("id", ParquetPhysicalType.Int32),
            ColumnDefinition.OptionalLeaf("name", ParquetPhysicalType.ByteArray,
                logicalType: new LogicalType.String()),
            ColumnDefinition.RequiredGroup("profile",
                ColumnDefinition.RequiredLeaf("active", ParquetPhysicalType.Boolean),
                ColumnDefinition.OptionalLeaf("born", ParquetPhysicalType.Int32,
                    logicalType: new LogicalType.Date())),
            ColumnDefinition.List("items",
                ColumnDefinition.RequiredGroup("item",
                    ColumnDefinition.RequiredLeaf("sku", ParquetPhysicalType.Int32),
                    ColumnDefinition.RequiredLeaf("note", ParquetPhysicalType.ByteArray,
                        logicalType: new LogicalType.String()))),
            ColumnDefinition.Map("scores",
                ColumnDefinition.RequiredLeaf("key", ParquetPhysicalType.ByteArray,
                    logicalType: new LogicalType.String()),
                ColumnDefinition.OptionalLeaf("value", ParquetPhysicalType.Int32)),
            ColumnDefinition.List("nested",
                ColumnDefinition.List("inner",
                    ColumnDefinition.RequiredLeaf("value", ParquetPhysicalType.Int32))),
            ColumnDefinition.List("optional_numbers",
                ColumnDefinition.OptionalLeaf("value", ParquetPhysicalType.Int32),
                repetition: ParquetRepetition.Optional),
            ColumnDefinition.RequiredLeaf("identifier", ParquetPhysicalType.FixedLenByteArray,
                new ColumnOptions(typeLength: 16), new LogicalType.Uuid())
        ]);

    static byte[] Write(ParquetSchema schema, params IReadOnlyDictionary<string, object?>[][] rowGroups)
    {
        using var stream = new MemoryStream();
        var writer = new ParquetUntypedWriter(stream, schema, new ParquetWriterOptions
        {
            Compression = CompressionKind.Snappy,
            TargetDataPageSizeBytes = 64
        });
        for (var i = 0; i < rowGroups.Length; i++)
            writer.WriteRowGroup(rowGroups[i]);
        writer.CloseFile();
        return stream.ToArray();
    }

    static void AssertValue(object? expected, object? actual, string path)
    {
        if (expected is IReadOnlyDictionary<string, object?> expectedGroup &&
            actual is IReadOnlyDictionary<string, object?> actualGroup)
        {
            if (expectedGroup.Count != actualGroup.Count)
                throw new InvalidOperationException($"{path} group size differs.");
            foreach (var pair in expectedGroup)
            {
                if (!actualGroup.TryGetValue(pair.Key, out var actualValue))
                    throw new InvalidOperationException($"{path}.{pair.Key} is missing.");
                AssertValue(pair.Value, actualValue, $"{path}.{pair.Key}");
            }
            return;
        }
        if (expected is System.Collections.IDictionary expectedMap &&
            actual is System.Collections.IDictionary actualMap)
        {
            if (expectedMap.Count != actualMap.Count)
                throw new InvalidOperationException($"{path} map size differs.");
            foreach (System.Collections.DictionaryEntry pair in expectedMap)
            {
                if (!actualMap.Contains(pair.Key))
                    throw new InvalidOperationException($"{path}[{pair.Key}] is missing.");
                AssertValue(pair.Value, actualMap[pair.Key], $"{path}[{pair.Key}]");
            }
            return;
        }
        if (expected is System.Collections.IEnumerable expectedSequence && expected is not string &&
            actual is System.Collections.IEnumerable actualSequence && actual is not string &&
            expected is not byte[] && actual is not byte[])
        {
            var expectedValues = expectedSequence.Cast<object?>().ToArray();
            var actualValues = actualSequence.Cast<object?>().ToArray();
            if (expectedValues.Length != actualValues.Length)
                throw new InvalidOperationException($"{path} sequence size differs.");
            for (var i = 0; i < expectedValues.Length; i++)
                AssertValue(expectedValues[i], actualValues[i], $"{path}[{i}]");
            return;
        }
        if (expected is IConvertible && actual is IConvertible &&
            expected.GetType() != actual.GetType())
        {
            if (Convert.ToDecimal(expected, System.Globalization.CultureInfo.InvariantCulture) ==
                Convert.ToDecimal(actual, System.Globalization.CultureInfo.InvariantCulture))
                return;
        }
        if (!Equals(expected, actual))
            throw new InvalidOperationException($"{path}: expected '{expected}', got '{actual}'.");
    }

    static void Expect<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }
}
