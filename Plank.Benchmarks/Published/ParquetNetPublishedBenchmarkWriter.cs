using Parquet;
using Parquet.Schema;
using ParquetDataField = Parquet.Schema.DataField;
using ParquetNetSchema = Parquet.Schema.ParquetSchema;

namespace Plank.Benchmarks.Published;

sealed class ParquetNetPublishedBenchmarkWriter : IPublishedBenchmarkWriter
{
    readonly PublishedBenchmarkDataSet _dataSet;
    readonly ParquetDataField[] _fields;
    readonly ParquetNetSchema _schema;

    public ParquetNetPublishedBenchmarkWriter(PublishedBenchmarkDataSet dataSet)
    {
        _dataSet = dataSet;
        _fields = dataSet.Columns.Select(CreateField).ToArray();
        _schema = new ParquetNetSchema(_fields);
    }

    public string ImplementationId
        => "parquetnet-single";

    public string Label
        => "Parquet.Net (1 thread)";

    public int Threads
        => 1;

    public bool IsSupported
        => _dataSet.Encoding switch
        {
            "plain" => true,
            "dictionary" => AllColumnsAre(BenchmarkColumnKind.String),
            "delta_binary_packed" => AllColumnsAre(BenchmarkColumnKind.Int32, BenchmarkColumnKind.Int64),
            "byte_stream_split" => AllColumnsAre(
                BenchmarkColumnKind.Int32, BenchmarkColumnKind.Int64, BenchmarkColumnKind.Double),
            _ => false
        };

    public string? UnavailableReason
        => IsSupported ? null : $"Parquet.Net 6.0.3 cannot request {_dataSet.Encoding} without falling back.";

    public void PrepareWrite()
    {
    }

    public async ValueTask WriteAsync(Stream destination, CancellationToken cancellationToken)
    {
        if (!IsSupported)
            throw new NotSupportedException(UnavailableReason);

        var options = CreateOptions(_dataSet);
        var writer = await Parquet.ParquetWriter.CreateAsync(_schema, destination, options, false, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            for (var rowGroupIndex = 0; rowGroupIndex < _dataSet.RowGroupCount; rowGroupIndex++)
            {
                using var rowGroup = writer.CreateRowGroup();
                for (var columnIndex = 0; columnIndex < _fields.Length; columnIndex++)
                {
                    await WriteColumnAsync(rowGroup, _fields[columnIndex], _dataSet.Columns[columnIndex], rowGroupIndex,
                        cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            await writer.DisposeAsync().ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
    }

    bool AllColumnsAre(params BenchmarkColumnKind[] kinds)
        => _dataSet.Columns.All(column => kinds.Contains(column.Kind));

    static Task WriteColumnAsync(ParquetRowGroupWriter rowGroup, ParquetDataField field,
        PublishedBenchmarkDataSet.Column column, int rowGroupIndex, CancellationToken cancellationToken)
        => (column.Kind, column.Nullable) switch
        {
            (BenchmarkColumnKind.Boolean, true) => rowGroup.WriteAsync<bool>(field,
                (bool?[])column.Values[rowGroupIndex], null, null, cancellationToken),
            (BenchmarkColumnKind.Boolean, false) => rowGroup.WriteAsync<bool>(field,
                (bool[])column.Values[rowGroupIndex], null, null, cancellationToken),
            (BenchmarkColumnKind.Int32, true) => rowGroup.WriteAsync<int>(field,
                (int?[])column.Values[rowGroupIndex], null, null, cancellationToken),
            (BenchmarkColumnKind.Int32, false) => rowGroup.WriteAsync<int>(field,
                (int[])column.Values[rowGroupIndex], null, null, cancellationToken),
            (BenchmarkColumnKind.Int64, true) => rowGroup.WriteAsync<long>(field,
                (long?[])column.Values[rowGroupIndex], null, null, cancellationToken),
            (BenchmarkColumnKind.Int64, false) => rowGroup.WriteAsync<long>(field,
                (long[])column.Values[rowGroupIndex], null, null, cancellationToken),
            (BenchmarkColumnKind.Timestamp, true) => rowGroup.WriteAsync<DateTime>(field,
                (DateTime?[])column.Values[rowGroupIndex], null, null, cancellationToken),
            (BenchmarkColumnKind.Timestamp, false) => rowGroup.WriteAsync<DateTime>(field,
                (DateTime[])column.Values[rowGroupIndex], null, null, cancellationToken),
            (BenchmarkColumnKind.Double, true) => rowGroup.WriteAsync<double>(field,
                (double?[])column.Values[rowGroupIndex], null, null, cancellationToken),
            (BenchmarkColumnKind.Double, false) => rowGroup.WriteAsync<double>(field,
                (double[])column.Values[rowGroupIndex], null, null, cancellationToken),
            (BenchmarkColumnKind.String, _) => rowGroup.WriteAsync(field,
                (string?[])column.Values[rowGroupIndex]),
            _ => throw new NotSupportedException($"Unsupported column kind '{column.Kind}'.")
        };

    static ParquetDataField CreateField(PublishedBenchmarkDataSet.Column column)
        => (column.Kind, column.Nullable) switch
        {
            (BenchmarkColumnKind.Boolean, true) => new DataField<bool?>(column.Name),
            (BenchmarkColumnKind.Boolean, false) => new DataField<bool>(column.Name),
            (BenchmarkColumnKind.Int32, true) => new DataField<int?>(column.Name),
            (BenchmarkColumnKind.Int32, false) => new DataField<int>(column.Name),
            (BenchmarkColumnKind.Int64, true) => new DataField<long?>(column.Name),
            (BenchmarkColumnKind.Int64, false) => new DataField<long>(column.Name),
            (BenchmarkColumnKind.Timestamp, _) => new DateTimeDataField(column.Name, DateTimeFormat.DateAndTimeMicros,
                false, DateTimeTimeUnit.Micros, column.Nullable),
            (BenchmarkColumnKind.Double, true) => new DataField<double?>(column.Name),
            (BenchmarkColumnKind.Double, false) => new DataField<double>(column.Name),
            (BenchmarkColumnKind.String, true) => new DataField<string?>(column.Name),
            (BenchmarkColumnKind.String, false) => new DataField<string>(column.Name),
            _ => throw new NotSupportedException($"Unsupported column kind '{column.Kind}'.")
        };

    static ParquetOptions CreateOptions(PublishedBenchmarkDataSet dataSet)
    {
        var options = new ParquetOptions
        {
            CompressionMethod = CompressionMethod.None,
            DictionaryEncodingThreshold = dataSet.Encoding == "dictionary" ? 1.0 : 0
        };
        var hint = dataSet.Encoding switch
        {
            "dictionary" => EncodingHint.Dictionary,
            "delta_binary_packed" => EncodingHint.DeltaBinaryPacked,
            "byte_stream_split" => EncodingHint.ByteSplitStream,
            _ => EncodingHint.Default
        };
        foreach (var column in dataSet.Columns)
            options.ColumnEncodingHints[column.Name] = hint;
        return options;
    }
}
