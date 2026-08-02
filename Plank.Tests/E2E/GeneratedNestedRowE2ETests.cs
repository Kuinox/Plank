using Parquet;
using ParquetSharp;
using Plank.Schema;
using Plank.Writing;

namespace Plank.Tests.E2E;

[NotInParallel]
internal sealed class GeneratedNestedRowE2ETests
{
    const int MultiRowGroupRowCount = 1031;

    [Test]
    public async Task GeneratedRowsRoundTripCollectionsMapsAndRecordsAcrossRowGroups()
    {
        var path = Path.Combine(Path.GetTempPath(), $"plank-generated-nested-{Guid.NewGuid():N}.parquet");
        try
        {
            using (var writeStream = File.Create(path))
            {
                var writer = GeneratedNestedRowSchema.CreateRowWriter(writeStream, maxParallelism: 1,
                    new ParquetWriterOptions { Compression = CompressionKind.None });
                for (var i = 0; i < MultiRowGroupRowCount; i++)
                {
                    var row = writer.GetRow();
                    row.Sequence = i;
                    row.CorrelationId = CreateGuid(i);
                    row.Label = $"row-{i}";
                    row.Values = CreateValues(i);
                    row.Scores = CreateScores(i);
                    row.Location = CreateLocation(i);
                    row.Items = CreateItems(i);
                    row.Names = CreateNames(i);
                    row.Identifiers = CreateIdentifiers(i);
                    row.Dates = CreateDates(i);
                    row.Times = CreateTimes(i);
                    row.Timestamps = CreateTimestamps(i);
                    row.Instants = CreateInstants(i);
                    writer.Next();
                }
                writer.Complete();
            }

            using (var readStream = File.OpenRead(path))
            using (var reader = GeneratedNestedRowSchema.CreateRowReader(readStream))
            {
                var rowIndex = 0;
                foreach (var row in reader)
                {
                    if (row.Sequence != rowIndex || row.CorrelationId != CreateGuid(rowIndex) ||
                        row.Label != $"row-{rowIndex}")
                        throw new InvalidOperationException($"Flat values differ for row {rowIndex}.");
                    AssertValues(row.Values, CreateValues(rowIndex), rowIndex);
                    AssertScores(row.Scores, CreateScores(rowIndex), rowIndex);
                    if (row.Location != CreateLocation(rowIndex))
                        throw new InvalidOperationException($"Location differs for row {rowIndex}.");
                    AssertItems(row.Items, CreateItems(rowIndex), rowIndex);
                    AssertList(row.Names, CreateNames(rowIndex), "Names", rowIndex);
                    AssertList(row.Identifiers, CreateIdentifiers(rowIndex), "Identifiers", rowIndex);
                    AssertList(row.Dates, CreateDates(rowIndex), "Dates", rowIndex);
                    AssertList(row.Times, CreateTimes(rowIndex), "Times", rowIndex);
                    AssertList(row.Timestamps, CreateTimestamps(rowIndex), "Timestamps", rowIndex);
                    AssertList(row.Instants, CreateInstants(rowIndex), "Instants", rowIndex);
                    rowIndex++;
                }
                if (rowIndex != MultiRowGroupRowCount)
                    throw new InvalidOperationException(
                        $"Expected {MultiRowGroupRowCount} generated rows, read {rowIndex}.");
            }

            using (var projectionStream = File.OpenRead(path))
            using (var reader = GeneratedNestedRowSchema.CreateRowReader(projectionStream,
                       GeneratedNestedRowSchema.Projection.Location | GeneratedNestedRowSchema.Projection.Items))
            {
                var rowIndex = 0;
                foreach (var row in reader)
                {
                    if (row.Location != CreateLocation(rowIndex))
                        throw new InvalidOperationException($"Projected location differs for row {rowIndex}.");
                    AssertItems(row.Items, CreateItems(rowIndex), rowIndex);
                    if (rowIndex == 0)
                    {
                        var rejected = false;
                        try
                        {
                            _ = row.Values;
                        }
                        catch (InvalidOperationException)
                        {
                            rejected = true;
                        }
                        if (!rejected)
                            throw new InvalidOperationException("An unselected generated nested property was readable.");
                    }
                    rowIndex++;
                }
                if (rowIndex != MultiRowGroupRowCount)
                    throw new InvalidOperationException(
                        $"Expected {MultiRowGroupRowCount} projected rows, read {rowIndex}.");

                using var resetStream = File.OpenRead(path);
                reader.Reset(resetStream, GeneratedNestedRowSchema.Projection.Values);
                if (!reader.MoveNext())
                    throw new InvalidOperationException("Reset generated reader did not return its first row.");
                AssertValues(reader.Current.Values, CreateValues(0), row: 0);
            }

            using var parquetNetStream = File.OpenRead(path);
            using var parquetNet = await ParquetReader.CreateAsync(parquetNetStream).ConfigureAwait(false);
            if (parquetNet.RowGroupCount < 2)
                throw new InvalidOperationException("Expected the generated pipeline to emit multiple row groups.");
            var fields = parquetNet.Schema.GetDataFields();
            if (fields.Length != GeneratedNestedRowSchema.Schema.LeafColumns.Length)
                throw new InvalidOperationException(
                    $"Parquet.Net found {fields.Length} leaves, expected {GeneratedNestedRowSchema.Schema.LeafColumns.Length}.");
            for (var rowGroupIndex = 0; rowGroupIndex < parquetNet.RowGroupCount; rowGroupIndex++)
            {
                using var rowGroup = parquetNet.OpenRowGroupReader(rowGroupIndex);
                for (var fieldIndex = 0; fieldIndex < fields.Length; fieldIndex++)
                    _ = await rowGroup.ReadColumnAsync(fields[fieldIndex]).ConfigureAwait(false);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    [Arguments(ParquetDataPageVersion.V1)]
    [Arguments(ParquetDataPageVersion.V2)]
    public void GeneratedListReaderReadsParquetSharpPages(ParquetDataPageVersion pageVersion)
    {
        var path = Path.Combine(Path.GetTempPath(), $"plank-generated-list-{pageVersion}-{Guid.NewGuid():N}.parquet");
        int?[]?[] rows = [[1, null, 2], null, [], [3]];
        try
        {
            using (var propertiesBuilder = new WriterPropertiesBuilder())
            using (var properties = propertiesBuilder
                       .Compression(Compression.Uncompressed)
                       .DataPageVersion(pageVersion)
                       .Build())
            using (var stream = File.Create(path))
            using (var writer = new ParquetFileWriter(stream,
                       [new ParquetSharp.Column<int?[]?>("Values")], null, properties, null, true))
            using (var rowGroup = writer.AppendRowGroup())
            using (var logical = rowGroup.NextColumn().LogicalWriter<int?[]?>())
                logical.WriteBatch(rows);

            using var readStream = File.OpenRead(path);
            using var reader = GeneratedListRowSchema.CreateRowReader(readStream, schemaEvolution:
                new Plank.Reading.ParquetSchemaEvolutionOptions
                {
                    LogicalTypes = Plank.Reading.SchemaTypeEvolutionBehavior.AllowCompatible
                });
            var rowIndex = 0;
            foreach (var row in reader)
            {
                AssertFlatValues(row.Values, rows[rowIndex], rowIndex);
                rowIndex++;
            }
            if (rowIndex != rows.Length)
                throw new InvalidOperationException($"Expected {rows.Length} rows, read {rowIndex}.");
        }
        finally
        {
            File.Delete(path);
        }
    }

    static int?[][]? CreateValues(int row)
        => (row % 4) switch
        {
            0 => null,
            1 => [],
            2 => [[row, null], []],
            _ => [[row + 1]]
        };

    static Dictionary<int, int?>? CreateScores(int row)
        => (row % 4) switch
        {
            0 => null,
            1 => [],
            2 => new Dictionary<int, int?> { [row] = row * 10, [row + 1] = null },
            _ => new Dictionary<int, int?> { [row] = row * 10 }
        };

    static GeneratedNestedAddress? CreateLocation(int row)
        => row % 3 == 0
            ? null
            : new GeneratedNestedAddress
            {
                Zip = 10000 + row,
                Rank = unchecked((byte)row),
                City = $"city-{row}",
                Token = CreateGuid(row + 10000)
            };

    static List<GeneratedNestedEntry>? CreateItems(int row)
        => (row % 4) switch
        {
            0 => null,
            1 => [],
            2 => [new GeneratedNestedEntry { Id = row, Amount = row * 100L }],
            _ =>
            [
                new GeneratedNestedEntry { Id = row, Amount = row * 100L },
                new GeneratedNestedEntry { Id = row + 1, Amount = (row + 1) * 100L }
            ]
        };

    static List<string?>? CreateNames(int row)
        => (row % 4) switch
        {
            0 => null,
            1 => [],
            2 => [$"row-{row}", null],
            _ => [$"row-{row}"]
        };

    static List<Guid?>? CreateIdentifiers(int row)
        => (row % 4) switch
        {
            0 => null,
            1 => [],
            2 => [CreateGuid(row), null],
            _ => [CreateGuid(row)]
        };

    static Guid CreateGuid(int row)
        => new(row, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    static List<DateOnly?>? CreateDates(int row)
        => (row % 4) switch
        {
            0 => null,
            1 => [],
            2 => [new DateOnly(2026, 1, 1).AddDays(row), null],
            _ => [new DateOnly(2026, 1, 1).AddDays(row)]
        };

    static List<DateTimeOffset?>? CreateInstants(int row)
        => (row % 4) switch
        {
            0 => null,
            1 => [],
            2 => [CreateInstant(row), null],
            _ => [CreateInstant(row)]
        };

    static List<TimeOnly?>? CreateTimes(int row)
        => (row % 4) switch
        {
            0 => null,
            1 => [],
            2 => [new TimeOnly(1, 2, 3).AddMinutes(row), null],
            _ => [new TimeOnly(1, 2, 3).AddMinutes(row)]
        };

    static List<DateTime?>? CreateTimestamps(int row)
        => (row % 4) switch
        {
            0 => null,
            1 => [],
            2 => [DateTime.UnixEpoch.AddMinutes(row), null],
            _ => [DateTime.UnixEpoch.AddMinutes(row)]
        };

    static DateTimeOffset CreateInstant(int row)
        => new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.FromHours(2)).AddMinutes(row);

    static void AssertValues(int?[][]? actual, int?[][]? expected, int row)
    {
        if (actual is null || expected is null)
        {
            if (actual is not null || expected is not null)
                throw new InvalidOperationException($"Values nullability differs for row {row}.");
            return;
        }
        if (actual.Length != expected.Length)
            throw new InvalidOperationException($"Values outer length differs for row {row}.");
        for (var i = 0; i < actual.Length; i++)
            AssertFlatValues(actual[i], expected[i], row);
    }

    static void AssertFlatValues(int?[]? actual, int?[]? expected, int row)
    {
        if (actual is null || expected is null)
        {
            if (actual is not null || expected is not null)
                throw new InvalidOperationException($"Value nullability differs for row {row}.");
            return;
        }
        if (!actual.AsSpan().SequenceEqual(expected))
            throw new InvalidOperationException($"Values differ for row {row}.");
    }

    static void AssertScores(Dictionary<int, int?>? actual, Dictionary<int, int?>? expected, int row)
    {
        if (actual is null || expected is null)
        {
            if (actual is not null || expected is not null)
                throw new InvalidOperationException($"Scores nullability differs for row {row}.");
            return;
        }
        if (actual.Count != expected.Count || expected.Any(pair =>
                !actual.TryGetValue(pair.Key, out var value) || value != pair.Value))
            throw new InvalidOperationException($"Scores differ for row {row}.");
    }

    static void AssertItems(List<GeneratedNestedEntry>? actual, List<GeneratedNestedEntry>? expected, int row)
    {
        if (actual is null || expected is null)
        {
            if (actual is not null || expected is not null)
                throw new InvalidOperationException($"Items nullability differs for row {row}.");
            return;
        }
        if (!actual.SequenceEqual(expected))
            throw new InvalidOperationException($"Items differ for row {row}.");
    }

    static void AssertList<T>(IReadOnlyList<T>? actual, IReadOnlyList<T>? expected, string property, int row)
    {
        if (actual is null || expected is null)
        {
            if (actual is not null || expected is not null)
                throw new InvalidOperationException($"{property} nullability differs for row {row}.");
            return;
        }
        if (actual.Count != expected.Count)
            throw new InvalidOperationException($"{property} count differs for row {row}.");
        for (var i = 0; i < actual.Count; i++)
            if (!EqualityComparer<T>.Default.Equals(actual[i], expected[i]))
                throw new InvalidOperationException($"{property} item {i} differs for row {row}.");
    }
}
