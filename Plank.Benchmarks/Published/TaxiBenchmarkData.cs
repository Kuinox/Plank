using System.Text;
using ParquetSharp;

namespace Plank.Benchmarks.Published;

public static class TaxiBenchmarkData
{
    const string January2024Url = "https://d37ci6vzurychx.cloudfront.net/trip-data/yellow_tripdata_2024-01.parquet";
    const string January2024File = "yellow_tripdata_2024-01.parquet";

    public static async Task<string> EnsureJanuary2024Async(string dataDirectory, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(dataDirectory);
        var path = Path.Combine(dataDirectory, January2024File);
        if (File.Exists(path))
            return path;

        Console.WriteLine($"Downloading {January2024Url}");
        using var client = new HttpClient();
        await using var source = await client.GetStreamAsync(January2024Url, cancellationToken).ConfigureAwait(false);
        await using var destination = File.Create(path);
        await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        return path;
    }

    internal static bool IsCaseId(string caseId)
        => GetCaseColumnOrdinals(caseId) is not null;

    public static IReadOnlyList<PublishedBenchmarkDataSet> Load(string path, int? maximumRows = null,
        string? caseId = null)
    {
        var selectedColumns = GetCaseColumnOrdinals(caseId) ?? [];
        using var reader = new ParquetFileReader(path);
        var columns = new PublishedBenchmarkDataSet.Column[19];
        var values = new List<Array>[19];
        var utf8Values = new List<Array>[19];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = [];
            utf8Values[i] = [];
        }

        var remaining = maximumRows ?? int.MaxValue;
        for (var rowGroupIndex = 0; rowGroupIndex < reader.FileMetaData.NumRowGroups && remaining > 0; rowGroupIndex++)
        {
            using var rowGroup = reader.RowGroup(rowGroupIndex);
            var count = Math.Min(checked((int)rowGroup.MetaData.NumRows), remaining);
            if (selectedColumns.Contains(0)) values[0].Add(Read<int?>(rowGroup, 0, count));
            if (selectedColumns.Contains(1)) values[1].Add(Read<DateTime?>(rowGroup, 1, count));
            if (selectedColumns.Contains(2)) values[2].Add(Read<DateTime?>(rowGroup, 2, count));
            if (selectedColumns.Contains(3)) values[3].Add(Read<long?>(rowGroup, 3, count));
            if (selectedColumns.Contains(4)) values[4].Add(Read<double?>(rowGroup, 4, count));
            if (selectedColumns.Contains(5)) values[5].Add(Read<long?>(rowGroup, 5, count));
            if (selectedColumns.Contains(6))
            {
                var strings = Read<string?>(rowGroup, 6, count);
                values[6].Add(strings);
                utf8Values[6].Add(ToUtf8(strings));
            }
            if (selectedColumns.Contains(7)) values[7].Add(Read<int?>(rowGroup, 7, count));
            if (selectedColumns.Contains(8)) values[8].Add(Read<int?>(rowGroup, 8, count));
            if (selectedColumns.Contains(9)) values[9].Add(Read<long?>(rowGroup, 9, count));
            for (var column = 10; column < 19; column++)
                if (selectedColumns.Contains(column))
                    values[column].Add(Read<double?>(rowGroup, column, count));
            remaining -= count;
        }

        columns[0] = Column("VendorID", BenchmarkColumnKind.Int32, values[0]);
        columns[1] = Column("tpep_pickup_datetime", BenchmarkColumnKind.Timestamp, values[1]);
        columns[2] = Column("tpep_dropoff_datetime", BenchmarkColumnKind.Timestamp, values[2]);
        columns[3] = Column("passenger_count", BenchmarkColumnKind.Int64, values[3]);
        columns[4] = Column("trip_distance", BenchmarkColumnKind.Double, values[4]);
        columns[5] = Column("RatecodeID", BenchmarkColumnKind.Int64, values[5]);
        columns[6] = new PublishedBenchmarkDataSet.Column
        {
            Name = "store_and_fwd_flag",
            Kind = BenchmarkColumnKind.String,
            Nullable = true,
            Values = values[6],
            Utf8Values = utf8Values[6]
        };
        columns[7] = Column("PULocationID", BenchmarkColumnKind.Int32, values[7]);
        columns[8] = Column("DOLocationID", BenchmarkColumnKind.Int32, values[8]);
        columns[9] = Column("payment_type", BenchmarkColumnKind.Int64, values[9]);
        columns[10] = Column("fare_amount", BenchmarkColumnKind.Double, values[10]);
        columns[11] = Column("extra", BenchmarkColumnKind.Double, values[11]);
        columns[12] = Column("mta_tax", BenchmarkColumnKind.Double, values[12]);
        columns[13] = Column("tip_amount", BenchmarkColumnKind.Double, values[13]);
        columns[14] = Column("tolls_amount", BenchmarkColumnKind.Double, values[14]);
        columns[15] = Column("improvement_surcharge", BenchmarkColumnKind.Double, values[15]);
        columns[16] = Column("total_amount", BenchmarkColumnKind.Double, values[16]);
        columns[17] = Column("congestion_surcharge", BenchmarkColumnKind.Double, values[17]);
        columns[18] = Column("Airport_fee", BenchmarkColumnKind.Double, values[18]);

        PublishedBenchmarkDataSet[] dataSets =
        [
            .. Variants("taxi", "Complete taxi file", columns, "plain", "dictionary"),
            .. Variants("int32", "int32", Select(columns, 0, 7, 8),
                "plain", "dictionary", "delta_binary_packed", "byte_stream_split"),
            .. Variants("int64", "int64", Select(columns, 3, 5, 9),
                "plain", "dictionary", "delta_binary_packed", "byte_stream_split"),
            .. Variants("timestamps", "Timestamps", Select(columns, 1, 2),
                "plain", "dictionary", "delta_binary_packed", "byte_stream_split"),
            .. Variants("doubles", "Doubles", Select(columns, 4, 10, 11, 12, 13, 14, 15, 16, 17, 18),
                "plain", "dictionary", "byte_stream_split"),
            .. Variants("strings", "Strings", Select(columns, 6),
                "plain", "dictionary", "delta_length_byte_array", "delta_byte_array")
        ];
        return dataSets.Where(dataSet => caseId is null || dataSet.Id == caseId).ToArray();
    }

    static int[]? GetCaseColumnOrdinals(string? caseId)
    {
        if (caseId is null)
            return Enumerable.Range(0, 19).ToArray();
        var separator = caseId.LastIndexOf('-');
        if (separator < 0)
            return null;
        var encoding = caseId[(separator + 1)..];
        var prefix = caseId[..separator];
        return (prefix, encoding) switch
        {
            ("taxi", "plain" or "dictionary") => Enumerable.Range(0, 19).ToArray(),
            ("int32", "plain" or "dictionary") => [0, 7, 8],
            ("int64", "plain" or "dictionary") => [3, 5, 9],
            ("timestamps", "plain" or "dictionary") => [1, 2],
            ("doubles", "plain" or "dictionary") => [4, 10, 11, 12, 13, 14, 15, 16, 17, 18],
            ("strings", "plain" or "dictionary") => [6],
            _ when caseId is "int32-delta-binary-packed" or "int32-byte-stream-split" => [0, 7, 8],
            _ when caseId is "int64-delta-binary-packed" or "int64-byte-stream-split" => [3, 5, 9],
            _ when caseId is "timestamps-delta-binary-packed" or "timestamps-byte-stream-split" => [1, 2],
            _ when caseId is "doubles-byte-stream-split" => [4, 10, 11, 12, 13, 14, 15, 16, 17, 18],
            _ when caseId is "strings-delta-length-byte-array" or "strings-delta-byte-array" => [6],
            _ => null
        };
    }

    static T[] Read<T>(RowGroupReader rowGroup, int columnIndex, int count)
    {
        using var column = rowGroup.Column(columnIndex).LogicalReader<T>();
        return column.ReadAll(count);
    }

    static byte[]?[] ToUtf8(string?[] values)
    {
        var result = new byte[]?[values.Length];
        for (var i = 0; i < values.Length; i++)
            if (values[i] is { } value)
                result[i] = System.Text.Encoding.UTF8.GetBytes(value);
        return result;
    }

    static PublishedBenchmarkDataSet.Column Column(string name, BenchmarkColumnKind kind, IReadOnlyList<Array> values)
        => new() { Name = name, Kind = kind, Nullable = true, Values = values };

    static PublishedBenchmarkDataSet.Column[] Select(PublishedBenchmarkDataSet.Column[] columns, params int[] ordinals)
        => ordinals.Select(ordinal => columns[ordinal]).ToArray();

    static IEnumerable<PublishedBenchmarkDataSet> Variants(string id, string label,
        IReadOnlyList<PublishedBenchmarkDataSet.Column> columns, params string[] encodings)
        => encodings.Select(encoding => new PublishedBenchmarkDataSet
        {
            SuiteId = "real-world",
            Id = $"{id}-{encoding.Replace('_', '-')}",
            Label = label,
            Encoding = encoding,
            ThroughputUnit = "million rows/s",
            Columns = columns
        });
}
