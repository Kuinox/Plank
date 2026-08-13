using ParquetSharp;
using ParquetEncoding = ParquetSharp.Encoding;

namespace Plank.Benchmarks.Published;

static class PublishedBenchmarkAuditor
{
    public static async Task<long> WriteAndValidateAsync(IPublishedBenchmarkWriter writer,
        PublishedBenchmarkDataSet dataSet, NonClosingMemoryStream stream, CancellationToken cancellationToken)
    {
        writer.PrepareWrite();
        stream.Reset();
        await writer.WriteAsync(stream, cancellationToken).ConfigureAwait(false);
        var outputBytes = stream.Length;
        stream.Position = 0;
        using var reader = new ParquetFileReader(stream, true);
        var metadata = reader.FileMetaData;
        AssertEqual(dataSet.Columns.Count, metadata.NumColumns, "column count");
        AssertEqual(dataSet.RowGroupCount, metadata.NumRowGroups, "row-group count");
        AssertEqual(dataSet.RowCount, metadata.NumRows, "row count");

        for (var rowGroupIndex = 0; rowGroupIndex < dataSet.RowGroupCount; rowGroupIndex++)
        {
            using var rowGroup = reader.RowGroup(rowGroupIndex);
            var expectedRows = dataSet.Columns[0].Values[rowGroupIndex].Length;
            AssertEqual(expectedRows, rowGroup.MetaData.NumRows, $"row count in row group {rowGroupIndex}");
            for (var columnIndex = 0; columnIndex < dataSet.Columns.Count; columnIndex++)
            {
                using var columnMetadata = rowGroup.MetaData.GetColumnChunkMetaData(columnIndex);
                AssertEqual(Compression.Uncompressed, columnMetadata.Compression,
                    $"compression for column {dataSet.Columns[columnIndex].Name}");
                if (!UsesRequestedEncoding(columnMetadata.Encodings, dataSet.Encoding))
                    throw new InvalidDataException(
                        $"{writer.Label} did not use requested encoding '{dataSet.Encoding}' for column " +
                        $"'{dataSet.Columns[columnIndex].Name}'. Actual: {string.Join(", ", columnMetadata.Encodings)}.");
                ValidateRepresentativeValues(rowGroup, dataSet.Columns[columnIndex], columnIndex, rowGroupIndex);
            }
        }
        return outputBytes;
    }

    static void ValidateRepresentativeValues(RowGroupReader rowGroup, PublishedBenchmarkDataSet.Column column,
        int columnIndex, int rowGroupIndex)
    {
        var expected = column.Values[rowGroupIndex];
        var actual = ReadAll(rowGroup, column, columnIndex, expected.Length);
        if (expected.Length == 0)
            return;
        var positions = new[] { 0, expected.Length / 2, expected.Length - 1 }.Distinct();
        foreach (var position in positions)
            if (!Equals(expected.GetValue(position), actual.GetValue(position)))
                throw new InvalidDataException(
                    $"Representative value mismatch for '{column.Name}' at row {position} of row group {rowGroupIndex}.");
    }

    static Array ReadAll(RowGroupReader rowGroup, PublishedBenchmarkDataSet.Column column, int columnIndex, int count)
        => (column.Kind, column.Nullable) switch
        {
            (BenchmarkColumnKind.Boolean, true) => Read<bool?>(rowGroup, columnIndex, count),
            (BenchmarkColumnKind.Boolean, false) => Read<bool>(rowGroup, columnIndex, count),
            (BenchmarkColumnKind.Int32, true) => Read<int?>(rowGroup, columnIndex, count),
            (BenchmarkColumnKind.Int32, false) => Read<int>(rowGroup, columnIndex, count),
            (BenchmarkColumnKind.Int64, true) => Read<long?>(rowGroup, columnIndex, count),
            (BenchmarkColumnKind.Int64, false) => Read<long>(rowGroup, columnIndex, count),
            (BenchmarkColumnKind.Timestamp, true) => Read<DateTime?>(rowGroup, columnIndex, count),
            (BenchmarkColumnKind.Timestamp, false) => Read<DateTime>(rowGroup, columnIndex, count),
            (BenchmarkColumnKind.Double, true) => Read<double?>(rowGroup, columnIndex, count),
            (BenchmarkColumnKind.Double, false) => Read<double>(rowGroup, columnIndex, count),
            (BenchmarkColumnKind.String, true) => Read<string?>(rowGroup, columnIndex, count),
            (BenchmarkColumnKind.String, false) => Read<string>(rowGroup, columnIndex, count),
            _ => throw new NotSupportedException($"Unsupported column kind '{column.Kind}'.")
        };

    static T[] Read<T>(RowGroupReader rowGroup, int columnIndex, int count)
    {
        using var reader = rowGroup.Column(columnIndex).LogicalReader<T>();
        return reader.ReadAll(count);
    }

    static bool UsesRequestedEncoding(IReadOnlyList<ParquetEncoding> encodings, string requested)
        => requested switch
        {
            "plain" => encodings.Contains(ParquetEncoding.Plain),
            "rle" => encodings.Contains(ParquetEncoding.Rle),
            "dictionary" => encodings.Contains(ParquetEncoding.RleDictionary) || encodings.Contains(ParquetEncoding.PlainDictionary),
            "delta_binary_packed" => encodings.Contains(ParquetEncoding.DeltaBinaryPacked),
            "delta_length_byte_array" => encodings.Contains(ParquetEncoding.DeltaLengthByteArray),
            "delta_byte_array" => encodings.Contains(ParquetEncoding.DeltaByteArray),
            "byte_stream_split" => encodings.Contains(ParquetEncoding.ByteStreamSplit),
            _ => false
        };

    static void AssertEqual<T>(T expected, T actual, string description)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidDataException($"Invalid {description}: expected {expected}, got {actual}.");
    }
}
