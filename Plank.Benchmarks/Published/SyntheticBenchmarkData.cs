using System.Text;

namespace Plank.Benchmarks.Published;

public static class SyntheticBenchmarkData
{
    public static IReadOnlyList<PublishedBenchmarkDataSet> Create(int rows, int width, string? caseId = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rows);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        (string Id, Func<PublishedBenchmarkDataSet> Create)[] cases =
        [
            ("boolean-plain", () => CreateBoolean("boolean-plain", "boolean · Plain", "plain", rows, width)),
            ("boolean-rle", () => CreateBoolean("boolean-rle", "boolean · RLE", "rle", rows, width)),
            ("int32-plain", () => CreateInt32("int32-plain", "int32 · Plain", "plain", rows, width,
                static i => unchecked((int)CreateHighEntropy(i)))),
            ("int32-dictionary", () => CreateInt32("int32-dictionary", "int32 · Dictionary", "dictionary", rows,
                width, static i => i % 2_048)),
            ("int32-delta-binary-packed", () => CreateInt32("int32-delta-binary-packed",
                "int32 · Delta binary packed", "delta_binary_packed", rows, width,
                static i => 1_700_000_000 + i * 3 + i % 7)),
            ("int32-byte-stream-split", () => CreateInt32("int32-byte-stream-split",
                "int32 · Byte stream split", "byte_stream_split", rows, width,
                static i => unchecked((int)CreateHighEntropy(i)))),
            ("int64-plain", () => CreateInt64("int64-plain", "int64 · Plain", "plain", rows, width,
                CreateHighEntropy)),
            ("int64-dictionary", () => CreateInt64("int64-dictionary", "int64 · Dictionary", "dictionary",
                rows, width, static i => i % 2_048)),
            ("int64-delta-binary-packed", () => CreateInt64("int64-delta-binary-packed",
                "int64 · Delta binary packed", "delta_binary_packed", rows, width,
                static i => 1_700_000_000L + i * 3L + i % 7)),
            ("int64-byte-stream-split", () => CreateInt64("int64-byte-stream-split",
                "int64 · Byte stream split", "byte_stream_split", rows, width, CreateHighEntropy)),
            ("timestamp-plain", () => CreateTimestamp("timestamp-plain", "timestamp · Plain", "plain", rows,
                width, CreateTimestampValue)),
            ("timestamp-dictionary", () => CreateTimestamp("timestamp-dictionary", "timestamp · Dictionary",
                "dictionary", rows, width, static i => CreateTimestampValue(i % 2_048))),
            ("timestamp-delta-binary-packed", () => CreateTimestamp("timestamp-delta-binary-packed",
                "timestamp · Delta binary packed", "delta_binary_packed", rows, width,
                static i => CreateTimestampValue(i * 3 + i % 7))),
            ("timestamp-byte-stream-split", () => CreateTimestamp("timestamp-byte-stream-split",
                "timestamp · Byte stream split", "byte_stream_split", rows, width, CreateTimestampValue)),
            ("double-plain", () => CreateDouble("double-plain", "double · Plain", "plain", rows, width,
                CreateDoubleValue)),
            ("double-dictionary", () => CreateDouble("double-dictionary", "double · Dictionary", "dictionary",
                rows, width, static i => i % 2_048 / 8.0)),
            ("double-byte-stream-split", () => CreateDouble("double-byte-stream-split",
                "double · Byte stream split", "byte_stream_split", rows, width, CreateDoubleValue)),
            ("string-plain", () => CreateString("string-plain", "string · Plain", "plain", rows, width,
                static i => $"record-{i:D10}-{unchecked((ulong)CreateHighEntropy(i)):x16}")),
            ("string-dictionary", () => CreateString("string-dictionary", "string · Dictionary", "dictionary",
                rows, width, static i => $"value-{i % 2_048}")),
            ("string-delta-length-byte-array", () => CreateString("string-delta-length-byte-array",
                "string · Delta length byte array", "delta_length_byte_array", rows, width,
                static i => new string((char)('a' + i % 26), 5 + i % 79))),
            ("string-delta-byte-array", () => CreateString("string-delta-byte-array", "string · Delta byte array",
                "delta_byte_array", rows, width,
                static i => $"events/2024/01/partition-{i % 32:D2}/record-{i:D10}")),
        ];
        return cases.Where(item => caseId is null || item.Id == caseId).Select(item => item.Create()).ToArray();
    }

    static PublishedBenchmarkDataSet CreateBoolean(string id, string label, string encoding, int rows, int width)
    {
        var columns = new PublishedBenchmarkDataSet.Column[width];
        for (var column = 0; column < width; column++)
        {
            var values = new bool[rows];
            for (var row = 0; row < rows; row++)
                values[row] = ((row + column * 17) / 128 & 1) == 0;
            columns[column] = RequiredColumn($"value_{column}", BenchmarkColumnKind.Boolean, values);
        }
        return CreateDataSet(id, label, encoding, columns);
    }

    static PublishedBenchmarkDataSet CreateInt64(string id, string label, string encoding, int rows, int width,
        Func<int, long> factory)
    {
        var columns = new PublishedBenchmarkDataSet.Column[width];
        for (var column = 0; column < width; column++)
        {
            var values = new long[rows];
            for (var row = 0; row < rows; row++)
                values[row] = factory(checked(row + column * rows));
            columns[column] = RequiredColumn($"value_{column}", BenchmarkColumnKind.Int64, values);
        }
        return CreateDataSet(id, label, encoding, columns);
    }

    static PublishedBenchmarkDataSet CreateInt32(string id, string label, string encoding, int rows, int width,
        Func<int, int> factory)
    {
        var columns = new PublishedBenchmarkDataSet.Column[width];
        for (var column = 0; column < width; column++)
        {
            var values = new int[rows];
            for (var row = 0; row < rows; row++)
                values[row] = factory(checked(row + column * rows));
            columns[column] = RequiredColumn($"value_{column}", BenchmarkColumnKind.Int32, values);
        }
        return CreateDataSet(id, label, encoding, columns);
    }

    static PublishedBenchmarkDataSet CreateTimestamp(string id, string label, string encoding, int rows, int width,
        Func<int, DateTime> factory)
    {
        var columns = new PublishedBenchmarkDataSet.Column[width];
        for (var column = 0; column < width; column++)
        {
            var values = new DateTime[rows];
            for (var row = 0; row < rows; row++)
                values[row] = factory(checked(row + column * rows));
            columns[column] = RequiredColumn($"value_{column}", BenchmarkColumnKind.Timestamp, values);
        }
        return CreateDataSet(id, label, encoding, columns);
    }

    static PublishedBenchmarkDataSet CreateDouble(string id, string label, string encoding, int rows, int width,
        Func<int, double> factory)
    {
        var columns = new PublishedBenchmarkDataSet.Column[width];
        for (var column = 0; column < width; column++)
        {
            var values = new double[rows];
            for (var row = 0; row < rows; row++)
                values[row] = factory(checked(row + column * rows));
            columns[column] = RequiredColumn($"value_{column}", BenchmarkColumnKind.Double, values);
        }
        return CreateDataSet(id, label, encoding, columns);
    }

    static PublishedBenchmarkDataSet CreateString(string id, string label, string encoding, int rows, int width,
        Func<int, string> factory)
    {
        var columns = new PublishedBenchmarkDataSet.Column[width];
        for (var column = 0; column < width; column++)
        {
            var values = new string[rows];
            var utf8 = new byte[rows][];
            // Dictionary inputs deliberately repeat a small logical domain. Prepare each distinct
            // string and UTF-8 payload once, just as the ParquetSharp adapter prepares its compact
            // Arrow array outside the timed write. Keeping a million separately allocated copies made
            // Plank benchmark heap locality rather than dictionary encoding under parallel load.
            Dictionary<string, (string Value, byte[] Utf8)>? preparedDictionaryValues =
                encoding == "dictionary" ? new(StringComparer.Ordinal) : null;
            for (var row = 0; row < rows; row++)
            {
                var value = factory(checked(row + column * rows));
                if (preparedDictionaryValues is not null
                    && preparedDictionaryValues.TryGetValue(value, out var prepared))
                {
                    values[row] = prepared.Value;
                    utf8[row] = prepared.Utf8;
                }
                else
                {
                    values[row] = value;
                    utf8[row] = Encoding.UTF8.GetBytes(value);
                    preparedDictionaryValues?.Add(value, (value, utf8[row]));
                }
            }
            columns[column] = new PublishedBenchmarkDataSet.Column
            {
                Name = $"value_{column}",
                Kind = BenchmarkColumnKind.String,
                Nullable = false,
                Values = [values],
                Utf8Values = [utf8]
            };
        }
        return CreateDataSet(id, label, encoding, columns);
    }

    static PublishedBenchmarkDataSet.Column RequiredColumn(string name, BenchmarkColumnKind kind, Array values)
        => new() { Name = name, Kind = kind, Nullable = false, Values = [values] };

    static PublishedBenchmarkDataSet CreateDataSet(string id, string label, string encoding,
        IReadOnlyList<PublishedBenchmarkDataSet.Column> columns)
        => new()
        {
            SuiteId = "synthetic",
            Id = id,
            Label = label,
            Encoding = encoding,
            ThroughputUnit = "million values/s",
            Columns = columns
        };

    static long CreateHighEntropy(int index)
    {
        var value = unchecked((ulong)index + 0x9e3779b97f4a7c15UL);
        value ^= value >> 30;
        value *= 0xbf58476d1ce4e5b9UL;
        value ^= value >> 27;
        value *= 0x94d049bb133111ebUL;
        value ^= value >> 31;
        return unchecked((long)value);
    }

    static DateTime CreateTimestampValue(int index)
        => new(checked(DateTime.UnixEpoch.Ticks + (long)index * TimeSpan.TicksPerMillisecond),
            DateTimeKind.Unspecified);

    static double CreateDoubleValue(int index)
    {
        var value = CreateHighEntropy(index);
        return Math.ScaleB((value & 0x000f_ffff_ffff_ffffL) / (double)(1L << 52), index % 31 - 15);
    }
}
