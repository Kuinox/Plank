using System.Collections.Immutable;
using Plank.Schema;
using Plank.Writing;
using Plank.Writing.PageStrategy;

namespace Plank.Benchmarks.Published;

sealed class PlankPublishedBenchmarkWriter : IPublishedBenchmarkWriter
{
    readonly PublishedBenchmarkDataSet _dataSet;
    readonly ParquetSchema _schema;
    readonly int _workerCount;

    public PlankPublishedBenchmarkWriter(PublishedBenchmarkDataSet dataSet, int workerCount)
    {
        _dataSet = dataSet;
        _workerCount = workerCount;
        _schema = new ParquetSchema(dataSet.Columns.Select(CreateDefinition).ToImmutableArray());
    }

    public string ImplementationId
        => _workerCount == 1 ? "plank-single" : "plank-multi";

    public string Label
        => _workerCount == 1 ? "Plank (1 thread)" : $"Plank ({_workerCount} threads)";

    public int Threads
        => _workerCount;

    public bool IsSupported
        => true;

    public string? UnavailableReason
        => null;

    public void PrepareWrite()
    {
    }

    public ValueTask WriteAsync(Stream destination, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var options = new ParquetWriterOptions
        {
            Compression = CompressionKind.None,
            DataPageVersion = ParquetDataPageVersion.V1,
            WritePageIndexes = false,
            WritePageCrc = false,
            Execution = new ParquetExecutionOptions { WorkerCount = _workerCount }
        };
        var writer = _schema.CreateWriter(destination, options);
        var serialized = new object[_dataSet.Columns.Count];
        for (var columnIndex = 0; columnIndex < serialized.Length; columnIndex++)
            serialized[columnIndex] = CreateSerialized(writer, _schema.LeafColumns[columnIndex], _dataSet.Columns[columnIndex]);

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = _workerCount,
            CancellationToken = cancellationToken
        };
        for (var rowGroupIndex = 0; rowGroupIndex < _dataSet.RowGroupCount; rowGroupIndex++)
        {
            if (_workerCount == 1)
                for (var columnIndex = 0; columnIndex < serialized.Length; columnIndex++)
                    Serialize(serialized[columnIndex], GetPlankValues(_dataSet.Columns[columnIndex], rowGroupIndex));
            else
                Parallel.For(0, serialized.Length, parallelOptions,
                    columnIndex => Serialize(serialized[columnIndex], GetPlankValues(_dataSet.Columns[columnIndex], rowGroupIndex)));

            var rowGroup = writer.StartRowGroup();
            for (var columnIndex = 0; columnIndex < serialized.Length; columnIndex++)
                Write(rowGroup, serialized[columnIndex]);
        }
        writer.CloseFile();
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
    }

    ColumnDefinition CreateDefinition(PublishedBenchmarkDataSet.Column column)
    {
        var repetition = column.Nullable ? ParquetRepetition.Optional : ParquetRepetition.Required;
        var options = new ColumnOptions(repetition, [MapEncoding(_dataSet.Encoding)]);
        IPageStrategy? pageStrategy = _dataSet.Encoding == "dictionary" ? ForceDictionaryPageStrategy.Shared : null;
        LogicalType? logicalType = column.Kind switch
        {
            BenchmarkColumnKind.Timestamp => new LogicalType.Timestamp(TimeUnit.Micros, false),
            BenchmarkColumnKind.String => new LogicalType.String(),
            _ => null
        };
        return ColumnDefinition.Leaf(column.Name, MapPhysicalType(column.Kind), options, logicalType, pageStrategy);
    }

    static object CreateSerialized(ParquetWriter writer, LeafColumn leaf, PublishedBenchmarkDataSet.Column column)
        => (column.Kind, column.Nullable) switch
        {
            (BenchmarkColumnKind.Boolean, true) => writer.CreateSerializedColumn<bool?>(leaf),
            (BenchmarkColumnKind.Boolean, false) => writer.CreateSerializedColumn<bool>(leaf),
            (BenchmarkColumnKind.Int32, true) => writer.CreateSerializedColumn<int?>(leaf),
            (BenchmarkColumnKind.Int32, false) => writer.CreateSerializedColumn<int>(leaf),
            (BenchmarkColumnKind.Int64, true) => writer.CreateSerializedColumn<long?>(leaf),
            (BenchmarkColumnKind.Int64, false) => writer.CreateSerializedColumn<long>(leaf),
            (BenchmarkColumnKind.Timestamp, true) => writer.CreateSerializedColumn<DateTime?>(leaf),
            (BenchmarkColumnKind.Timestamp, false) => writer.CreateSerializedColumn<DateTime>(leaf),
            (BenchmarkColumnKind.Double, true) => writer.CreateSerializedColumn<double?>(leaf),
            (BenchmarkColumnKind.Double, false) => writer.CreateSerializedColumn<double>(leaf),
            (BenchmarkColumnKind.String, _) => writer.CreateSerializedColumn<byte[]?>(leaf),
            _ => throw new NotSupportedException($"Unsupported column kind '{column.Kind}'.")
        };

    static Array GetPlankValues(PublishedBenchmarkDataSet.Column column, int rowGroupIndex)
        => column.Kind == BenchmarkColumnKind.String
            ? column.Utf8Values?[rowGroupIndex]
              ?? throw new InvalidOperationException($"Column '{column.Name}' has no prepared UTF-8 values.")
            : column.Values[rowGroupIndex];

    static void Serialize(object serialized, Array values)
    {
        switch (serialized)
        {
            case SerializedColumn<bool?> column: column.Serialize((bool?[])values); break;
            case SerializedColumn<bool> column: column.Serialize((bool[])values); break;
            case SerializedColumn<int?> column: column.Serialize((int?[])values); break;
            case SerializedColumn<int> column: column.Serialize((int[])values); break;
            case SerializedColumn<long?> column: column.Serialize((long?[])values); break;
            case SerializedColumn<long> column: column.Serialize((long[])values); break;
            case SerializedColumn<DateTime?> column: column.Serialize((DateTime?[])values); break;
            case SerializedColumn<DateTime> column: column.Serialize((DateTime[])values); break;
            case SerializedColumn<double?> column: column.Serialize((double?[])values); break;
            case SerializedColumn<double> column: column.Serialize((double[])values); break;
            case SerializedColumn<byte[]?> column: column.Serialize((byte[]?[])values); break;
            default: throw new NotSupportedException($"Unsupported serialized column '{serialized.GetType()}'.");
        }
    }

    static void Write(RowGroupWriter rowGroup, object serialized)
    {
        switch (serialized)
        {
            case SerializedColumn<bool?> column: rowGroup.Write(column); break;
            case SerializedColumn<bool> column: rowGroup.Write(column); break;
            case SerializedColumn<int?> column: rowGroup.Write(column); break;
            case SerializedColumn<int> column: rowGroup.Write(column); break;
            case SerializedColumn<long?> column: rowGroup.Write(column); break;
            case SerializedColumn<long> column: rowGroup.Write(column); break;
            case SerializedColumn<DateTime?> column: rowGroup.Write(column); break;
            case SerializedColumn<DateTime> column: rowGroup.Write(column); break;
            case SerializedColumn<double?> column: rowGroup.Write(column); break;
            case SerializedColumn<double> column: rowGroup.Write(column); break;
            case SerializedColumn<byte[]?> column: rowGroup.Write(column); break;
            default: throw new NotSupportedException($"Unsupported serialized column '{serialized.GetType()}'.");
        }
    }

    static ParquetPhysicalType MapPhysicalType(BenchmarkColumnKind kind)
        => kind switch
        {
            BenchmarkColumnKind.Boolean => ParquetPhysicalType.Boolean,
            BenchmarkColumnKind.Int32 => ParquetPhysicalType.Int32,
            BenchmarkColumnKind.Int64 or BenchmarkColumnKind.Timestamp => ParquetPhysicalType.Int64,
            BenchmarkColumnKind.Double => ParquetPhysicalType.Double,
            BenchmarkColumnKind.String => ParquetPhysicalType.ByteArray,
            _ => throw new NotSupportedException($"Unsupported column kind '{kind}'.")
        };

    static EncodingKind MapEncoding(string encoding)
        => encoding switch
        {
            "plain" => EncodingKind.Plain,
            "rle" => EncodingKind.Rle,
            "dictionary" => EncodingKind.RleDictionary,
            "delta_binary_packed" => EncodingKind.DeltaBinaryPacked,
            "delta_length_byte_array" => EncodingKind.DeltaLengthByteArray,
            "delta_byte_array" => EncodingKind.DeltaByteArray,
            "byte_stream_split" => EncodingKind.ByteStreamSplit,
            _ => throw new NotSupportedException($"Unsupported encoding '{encoding}'.")
        };
}
