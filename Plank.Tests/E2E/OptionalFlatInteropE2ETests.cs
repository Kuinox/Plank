using Parquet;
using Parquet.Schema;
using ParquetSharp;
using Plank.Schema;
using Plank.Writing;
using PlankColumn = Plank.Schema.Column;
using PlankLogicalType = Plank.Schema.LogicalType;
using PlankParquetSchema = Plank.Schema.ParquetSchema;

namespace Plank.Tests.E2E;

internal sealed class OptionalFlatInteropE2ETests
{
    [Test]
    public async Task OptionalFlatColumnsWithNoNullsAreReadableByInteropReaders()
    {
        var path = Path.Combine(Path.GetTempPath(), $"plank-optional-flat-no-nulls-{Guid.NewGuid():N}.parquet");
        int?[] expectedIds = [10, 20, 30, 40];
        string[] expectedNames = ["alpha", "beta", "gamma", "delta"];

        try
        {
            WriteOptionalFlatFile(path, expectedIds, expectedNames);
            AssertParquetSharp(path, expectedIds, expectedNames);
            await AssertParquetNetAsync(path, expectedIds, expectedNames).ConfigureAwait(false);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    static void WriteOptionalFlatFile(string path, int?[] ids, string[] names)
    {
        var schema = new PlankParquetSchema([
            new PlankColumn("id", ParquetPhysicalType.Int32, new ColumnOptions(ParquetRepetition.Optional)),
            new PlankColumn("name", ParquetPhysicalType.ByteArray, new ColumnOptions(ParquetRepetition.Optional),
                new PlankLogicalType.String())
        ]);

        using var stream = File.Create(path);
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions());
        var idColumn = writer.CreateSerializedColumn<int?>(schema.Columns[0]);
        var nameColumn = writer.CreateSerializedColumn<string>(schema.Columns[1]);
        var rowGroup = writer.StartRowGroup();
        idColumn.Serialize(ids);
        nameColumn.Serialize(names);
        rowGroup.Write(idColumn);
        rowGroup.Write(nameColumn);
        writer.CloseFile();
    }

    static void AssertParquetSharp(string path, IReadOnlyList<int?> expectedIds, IReadOnlyList<string> expectedNames)
    {
        using var reader = new ParquetFileReader(path);
        using var rowGroup = reader.RowGroup(0);
        var rowCount = checked((int)rowGroup.MetaData.NumRows);
        var actualIds = rowGroup.Column(0).LogicalReader<int?>().ReadAll(rowCount);
        var actualNames = rowGroup.Column(1).LogicalReader<string>().ReadAll(rowCount);

        AssertNullableValues(actualIds, expectedIds, "ParquetSharp id");
        AssertValues(actualNames, expectedNames, "ParquetSharp name");
    }

    static async Task AssertParquetNetAsync(string path, IReadOnlyList<int?> expectedIds, IReadOnlyList<string> expectedNames)
    {
        using var stream = File.OpenRead(path);
        using var reader = await ParquetReader.CreateAsync(stream).ConfigureAwait(false);
        var fields = reader.Schema.GetDataFields();
        using var rowGroup = reader.OpenRowGroupReader(0);
        var idColumn = await rowGroup.ReadColumnAsync(GetField(fields, "id")).ConfigureAwait(false);
        var nameColumn = await rowGroup.ReadColumnAsync(GetField(fields, "name")).ConfigureAwait(false);

        var actualIds = idColumn.Data is int?[] nullableIds
            ? nullableIds
            : idColumn.Data.Cast<int>().Select(static value => (int?)value).ToArray();
        var actualNames = nameColumn.Data.Cast<string>().ToArray();

        AssertNullableValues(actualIds, expectedIds, "Parquet.Net id");
        AssertValues(actualNames, expectedNames, "Parquet.Net name");
    }

    static DataField GetField(DataField[] fields, string name)
    {
        for (var i = 0; i < fields.Length; i++)
            if (fields[i].Name == name)
                return fields[i];

        throw new InvalidOperationException($"Could not find field '{name}'.");
    }

    static void AssertNullableValues<T>(IReadOnlyList<T?> actual, IReadOnlyList<T?> expected, string label)
        where T : struct
    {
        if (actual.Count != expected.Count)
            throw new InvalidOperationException($"{label} length mismatch. Expected {expected.Count}, got {actual.Count}.");

        for (var i = 0; i < expected.Count; i++)
            if (!EqualityComparer<T?>.Default.Equals(actual[i], expected[i]))
                throw new InvalidOperationException($"{label} mismatch at index {i}. Expected '{expected[i]}', got '{actual[i]}'.");
    }

    static void AssertValues<T>(IReadOnlyList<T> actual, IReadOnlyList<T> expected, string label)
    {
        if (actual.Count != expected.Count)
            throw new InvalidOperationException($"{label} length mismatch. Expected {expected.Count}, got {actual.Count}.");

        for (var i = 0; i < expected.Count; i++)
            if (!EqualityComparer<T>.Default.Equals(actual[i], expected[i]))
                throw new InvalidOperationException($"{label} mismatch at index {i}. Expected '{expected[i]}', got '{actual[i]}'.");
    }
}
