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
    // Master arrays for column kinds whose benchmark values are already physical. The Arrow writer takes ownership
    // of whatever it is handed, so these are never written directly: PrepareWrite clones them, untimed.
    readonly IArrowArray?[][] _preparedArrays;
    IArrowArray?[][]? _clonedArrays;

    public ParquetSharpPublishedBenchmarkWriter(PublishedBenchmarkDataSet dataSet, bool useThreads, int workerCount)
    {
        _dataSet = dataSet;
        _useThreads = useThreads;
        _workerCount = workerCount;
        _schema = new ArrowSchema(dataSet.Columns.Select(CreateField), null);
        _preparedArrays = CreatePreparedArrays(dataSet);
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
        DisposeClonedArrays();
        _clonedArrays = _preparedArrays
            .Select(static arrays => arrays.Select(static array => Clone(array)).ToArray())
            .ToArray();
    }

    public ValueTask WriteAsync(Stream destination, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var cloned = _clonedArrays
            ?? throw new InvalidOperationException("Call PrepareWrite before writing a ParquetSharp benchmark file.");
        var batches = new RecordBatch[cloned.Length];
        try
        {
            using var writerProperties = CreateWriterProperties(_dataSet.Encoding);
            using var arrowProperties = new ArrowWriterPropertiesBuilder().UseThreads(_useThreads).Build();
            using var writer = new ArrowFileWriter(destination, _schema, writerProperties, arrowProperties, true);
            for (var rowGroupIndex = 0; rowGroupIndex < batches.Length; rowGroupIndex++)
            {
                batches[rowGroupIndex] = CreateBatch(cloned[rowGroupIndex], rowGroupIndex);
                if (rowGroupIndex != 0)
                    writer.NewBufferedRowGroup();
                writer.WriteBufferedRecordBatch(batches[rowGroupIndex]);
            }
            writer.Close();
            return ValueTask.CompletedTask;
        }
        finally
        {
            foreach (var batch in batches)
                batch?.Dispose();
            _clonedArrays = null;
        }
    }

    public void Dispose()
    {
        DisposeClonedArrays();
        foreach (var arrays in _preparedArrays)
            foreach (var array in arrays)
                array?.Dispose();
    }

    // Timestamp arrays are built here, inside the timed write, because turning DateTime into microseconds is work
    // the library does for the caller. Every other column was cloned by PrepareWrite, outside the stopwatch.
    RecordBatch CreateBatch(IArrowArray?[] cloned, int rowGroupIndex)
    {
        var arrays = new IArrowArray[cloned.Length];
        for (var columnIndex = 0; columnIndex < arrays.Length; columnIndex++)
            arrays[columnIndex] = cloned[columnIndex]
                ?? CreateTimestampArray(_dataSet.Columns[columnIndex], rowGroupIndex);
        return new RecordBatch(_schema, arrays, arrays[0].Length);
    }

    static IArrowArray? Clone(IArrowArray? array)
        => array is null ? null : ArrowArrayFactory.BuildArray(array.Data.Clone());

    void DisposeClonedArrays()
    {
        if (_clonedArrays is null)
            return;
        foreach (var arrays in _clonedArrays)
            foreach (var array in arrays)
                array?.Dispose();
        _clonedArrays = null;
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

    static IArrowArray?[][] CreatePreparedArrays(PublishedBenchmarkDataSet dataSet)
    {
        var prepared = new IArrowArray?[dataSet.RowGroupCount][];
        for (var rowGroupIndex = 0; rowGroupIndex < prepared.Length; rowGroupIndex++)
        {
            prepared[rowGroupIndex] = new IArrowArray?[dataSet.Columns.Count];
            for (var columnIndex = 0; columnIndex < prepared[rowGroupIndex].Length; columnIndex++)
                prepared[rowGroupIndex][columnIndex] = CreatePreparedArray(dataSet.Columns[columnIndex], rowGroupIndex);
        }
        return prepared;
    }

    // Returns null for timestamp columns: those are built by the timed write instead.
    static IArrowArray? CreatePreparedArray(PublishedBenchmarkDataSet.Column column, int rowGroupIndex)
        => (column.Kind, column.Nullable) switch
        {
            (BenchmarkColumnKind.Boolean, true) => CreateBoolean((bool?[])column.Values[rowGroupIndex]),
            (BenchmarkColumnKind.Boolean, false) => new BooleanArray.Builder().Append((bool[])column.Values[rowGroupIndex]).Build(),
            (BenchmarkColumnKind.Int32, true) => CreateInt32((int?[])column.Values[rowGroupIndex]),
            (BenchmarkColumnKind.Int32, false) => new Int32Array.Builder().Append((int[])column.Values[rowGroupIndex]).Build(),
            (BenchmarkColumnKind.Int64, true) => CreateInt64((long?[])column.Values[rowGroupIndex]),
            (BenchmarkColumnKind.Int64, false) => new Int64Array.Builder().Append((long[])column.Values[rowGroupIndex]).Build(),
            (BenchmarkColumnKind.Timestamp, _) => null,
            (BenchmarkColumnKind.Double, true) => CreateDouble((double?[])column.Values[rowGroupIndex]),
            (BenchmarkColumnKind.Double, false) => new DoubleArray.Builder().Append((double[])column.Values[rowGroupIndex]).Build(),
            (BenchmarkColumnKind.String, true) => CreateString((string?[])column.Values[rowGroupIndex]),
            (BenchmarkColumnKind.String, false) => CreateString((string[])column.Values[rowGroupIndex]),
            _ => throw new NotSupportedException($"Unsupported column kind '{column.Kind}'.")
        };

    static IArrowArray CreateTimestampArray(PublishedBenchmarkDataSet.Column column, int rowGroupIndex)
        => column.Nullable
            ? CreateTimestamp((DateTime?[])column.Values[rowGroupIndex])
            : CreateTimestamp((DateTime[])column.Values[rowGroupIndex]);

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
