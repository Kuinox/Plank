using Apache.Arrow;
using Apache.Arrow.Types;
using ParquetSharp;
using ParquetSharp.Arrow;
using ArrowFileWriter = ParquetSharp.Arrow.FileWriter;
using ArrowSchema = Apache.Arrow.Schema;
using ArrowTimeUnit = Apache.Arrow.Types.TimeUnit;
using ParquetEncoding = ParquetSharp.Encoding;

namespace Plank.Benchmarks.Published;

sealed class ParquetSharpPublishedBenchmarkWriter : IPublishedBenchmarkWriter
{
    readonly PublishedBenchmarkDataSet _dataSet;
    readonly bool _useThreads;
    readonly int _workerCount;
    readonly ArrowSchema _schema;
    readonly RecordBatch[] _batches;
    RecordBatch[]? _preparedBatches;

    public ParquetSharpPublishedBenchmarkWriter(PublishedBenchmarkDataSet dataSet, bool useThreads, int workerCount)
    {
        _dataSet = dataSet;
        _useThreads = useThreads;
        _workerCount = workerCount;
        _schema = new ArrowSchema(dataSet.Columns.Select(CreateField), null);
        _batches = CreateBatches(dataSet, _schema);
    }

    public string ImplementationId
        => _useThreads ? "parquetsharp-multi" : "parquetsharp-single";

    public string Label
        => _useThreads ? $"ParquetSharp ({_workerCount} threads)" : "ParquetSharp (1 thread)";

    public int Threads
        => _useThreads ? _workerCount : 1;

    public bool IsSupported
        => true;

    public string? UnavailableReason
        => null;

    public void PrepareWrite()
    {
        DisposePreparedBatches();
        _preparedBatches = _batches.Select(static batch => batch.Clone()).ToArray();
    }

    public ValueTask WriteAsync(Stream destination, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var batches = _preparedBatches
            ?? throw new InvalidOperationException("Call PrepareWrite before writing a ParquetSharp benchmark file.");
        try
        {
            using var writerProperties = CreateWriterProperties(_dataSet.Encoding);
            using var arrowProperties = new ArrowWriterPropertiesBuilder().UseThreads(_useThreads).Build();
            using var writer = new ArrowFileWriter(destination, _schema, writerProperties, arrowProperties, true);
            for (var rowGroupIndex = 0; rowGroupIndex < batches.Length; rowGroupIndex++)
            {
                if (rowGroupIndex != 0)
                    writer.NewBufferedRowGroup();
                writer.WriteBufferedRecordBatch(batches[rowGroupIndex]);
            }
            writer.Close();
            return ValueTask.CompletedTask;
        }
        finally
        {
            DisposePreparedBatches();
        }
    }

    public void Dispose()
    {
        DisposePreparedBatches();
        foreach (var batch in _batches)
            batch.Dispose();
    }

    void DisposePreparedBatches()
    {
        if (_preparedBatches is null)
            return;
        foreach (var batch in _preparedBatches)
            batch.Dispose();
        _preparedBatches = null;
    }

    static Field CreateField(PublishedBenchmarkDataSet.Column column)
        => new(column.Name, column.Kind switch
        {
            BenchmarkColumnKind.Boolean => (IArrowType)BooleanType.Default,
            BenchmarkColumnKind.Int32 => (IArrowType)Int32Type.Default,
            BenchmarkColumnKind.Int64 => Int64Type.Default,
            BenchmarkColumnKind.Timestamp => new TimestampType(ArrowTimeUnit.Microsecond, (string?)null),
            BenchmarkColumnKind.Double => DoubleType.Default,
            BenchmarkColumnKind.String => StringType.Default,
            _ => throw new NotSupportedException($"Unsupported column kind '{column.Kind}'.")
        }, column.Nullable);

    static RecordBatch[] CreateBatches(PublishedBenchmarkDataSet dataSet, ArrowSchema schema)
    {
        var batches = new RecordBatch[dataSet.RowGroupCount];
        for (var rowGroupIndex = 0; rowGroupIndex < batches.Length; rowGroupIndex++)
        {
            var arrays = new IArrowArray[dataSet.Columns.Count];
            for (var columnIndex = 0; columnIndex < arrays.Length; columnIndex++)
                arrays[columnIndex] = CreateArray(dataSet.Columns[columnIndex], rowGroupIndex);
            batches[rowGroupIndex] = new RecordBatch(schema, arrays, arrays[0].Length);
        }
        return batches;
    }

    static IArrowArray CreateArray(PublishedBenchmarkDataSet.Column column, int rowGroupIndex)
        => (column.Kind, column.Nullable) switch
        {
            (BenchmarkColumnKind.Boolean, true) => CreateBoolean((bool?[])column.Values[rowGroupIndex]),
            (BenchmarkColumnKind.Boolean, false) => new BooleanArray.Builder().Append((bool[])column.Values[rowGroupIndex]).Build(),
            (BenchmarkColumnKind.Int32, true) => CreateInt32((int?[])column.Values[rowGroupIndex]),
            (BenchmarkColumnKind.Int32, false) => new Int32Array.Builder().Append((int[])column.Values[rowGroupIndex]).Build(),
            (BenchmarkColumnKind.Int64, true) => CreateInt64((long?[])column.Values[rowGroupIndex]),
            (BenchmarkColumnKind.Int64, false) => new Int64Array.Builder().Append((long[])column.Values[rowGroupIndex]).Build(),
            (BenchmarkColumnKind.Timestamp, true) => CreateTimestamp((DateTime?[])column.Values[rowGroupIndex]),
            (BenchmarkColumnKind.Timestamp, false) => CreateTimestamp((DateTime[])column.Values[rowGroupIndex]),
            (BenchmarkColumnKind.Double, true) => CreateDouble((double?[])column.Values[rowGroupIndex]),
            (BenchmarkColumnKind.Double, false) => new DoubleArray.Builder().Append((double[])column.Values[rowGroupIndex]).Build(),
            (BenchmarkColumnKind.String, true) => CreateString((string?[])column.Values[rowGroupIndex]),
            (BenchmarkColumnKind.String, false) => CreateString((string[])column.Values[rowGroupIndex]),
            _ => throw new NotSupportedException($"Unsupported column kind '{column.Kind}'.")
        };

    static BooleanArray CreateBoolean(bool?[] values)
    {
        var builder = new BooleanArray.Builder();
        foreach (var value in values)
            if (value.HasValue)
                builder.Append(value.Value);
            else
                builder.AppendNull();
        return builder.Build();
    }

    static Int32Array CreateInt32(int?[] values)
    {
        var builder = new Int32Array.Builder();
        foreach (var value in values)
            builder.Append(value);
        return builder.Build();
    }

    static Int64Array CreateInt64(long?[] values)
    {
        var builder = new Int64Array.Builder();
        foreach (var value in values)
            builder.Append(value);
        return builder.Build();
    }

    static DoubleArray CreateDouble(double?[] values)
    {
        var builder = new DoubleArray.Builder();
        foreach (var value in values)
            builder.Append(value);
        return builder.Build();
    }

    static TimestampArray CreateTimestamp(DateTime?[] values)
    {
        var builder = new TimestampArray.Builder(ArrowTimeUnit.Microsecond);
        foreach (var value in values)
            if (value.HasValue)
                builder.Append(ToDateTimeOffset(value.Value));
            else
                builder.AppendNull();
        return builder.Build();
    }

    static TimestampArray CreateTimestamp(DateTime[] values)
    {
        var builder = new TimestampArray.Builder(ArrowTimeUnit.Microsecond);
        foreach (var value in values)
            builder.Append(ToDateTimeOffset(value));
        return builder.Build();
    }

    static StringArray CreateString(string?[] values)
    {
        var builder = new StringArray.Builder();
        foreach (var value in values)
            if (value is null)
                builder.AppendNull();
            else
                builder.Append(value);
        return builder.Build();
    }

    static DateTimeOffset ToDateTimeOffset(DateTime value)
        => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    static WriterProperties CreateWriterProperties(string encoding)
    {
        var builder = new WriterPropertiesBuilder()
            .Compression(Compression.Uncompressed)
            .DataPageVersion(ParquetDataPageVersion.V1)
            .DisableWritePageIndex()
            .DisablePageChecksum();
        if (encoding == "dictionary")
            return builder.EnableDictionary().DictionaryPagesizeLimit(long.MaxValue).Build();

        return builder.DisableDictionary().Encoding(encoding switch
        {
            "plain" => ParquetEncoding.Plain,
            "rle" => ParquetEncoding.Rle,
            "delta_binary_packed" => ParquetEncoding.DeltaBinaryPacked,
            "delta_length_byte_array" => ParquetEncoding.DeltaLengthByteArray,
            "delta_byte_array" => ParquetEncoding.DeltaByteArray,
            "byte_stream_split" => ParquetEncoding.ByteStreamSplit,
            _ => throw new NotSupportedException($"Unsupported encoding '{encoding}'.")
        }).Build();
    }
}
