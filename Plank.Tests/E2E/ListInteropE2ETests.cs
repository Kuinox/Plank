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
            await AssertParquetNetAsync(path, rows).ConfigureAwait(false);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    static async Task AssertParquetNetAsync(string path, int[][] expectedRows)
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

        var flattenedCount = 0;
        for (var i = 0; i < expectedRows.Length; i++)
            flattenedCount += expectedRows[i].Length;
        if (rep.Length != flattenedCount)
            throw new InvalidOperationException($"Expected {flattenedCount} repetition levels, got {rep.Length}.");
        if (def.Length != flattenedCount)
            throw new InvalidOperationException($"Expected {flattenedCount} definition levels, got {def.Length}.");
        if (values.Length != expectedRows.Length)
            throw new InvalidOperationException(
                $"Parquet.Net list projection changed. Expected {expectedRows.Length} projected values, got {values.Length}.");
    }

    static void AssertParquetSharp(string path, int[][] expectedRows)
    {
        using var reader = new ParquetFileReader(path);
        using var rowGroup = reader.RowGroup(0);
        using var logical = rowGroup.Column(0).LogicalReader<int[]>();
        var actualRows = logical.ReadAll(expectedRows.Length);
        AssertJaggedEqual(expectedRows, actualRows);
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
