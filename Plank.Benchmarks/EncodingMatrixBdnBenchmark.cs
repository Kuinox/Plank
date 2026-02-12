using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using Parquet;
using Parquet.Schema;
using ParquetSharp;
using Plank.Schema;
using Plank.Writing;
using CompressionMethod = Parquet.CompressionMethod;
using DataColumn = Parquet.Data.DataColumn;
using Encoding = ParquetSharp.Encoding;
using ParquetSchema = Parquet.Schema.ParquetSchema;
using PlankColumn = Plank.Schema.Column;
using PlankSchema = Plank.Schema.ParquetSchema;

namespace Plank.Benchmarks;

[Config(typeof(EncodingMatrixBenchmarkConfig))]
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput)]
[InvocationCount(100)]
public class EncodingMatrixBdnBenchmark
{
    [Params("bool", "int32", "int64", "float", "double", "string")]
    public string DataType { get; set; } = "int32";

    [Params("plain")]
    public string EncodingName { get; set; } = "plain";

    bool[] _boolValues = [];
    int[] _int32Values = [];
    long[] _int64Values = [];
    float[] _floatValues = [];
    double[] _doubleValues = [];
    string[] _stringValues = [];
    byte[] _lastParquetBuffer = [];
    string _currentLibrary = string.Empty;

    [Params(1_000_000)] public int Rows { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        _boolValues = new bool[Rows];
        _int32Values = new int[Rows];
        _int64Values = new long[Rows];
        _floatValues = new float[Rows];
        _doubleValues = new double[Rows];
        _stringValues = new string[Rows];
        for (var i = 0; i < Rows; i++)
        {
            _boolValues[i] = (i & 1) == 0;
            _int32Values[i] = i % 100_000;
            _int64Values[i] = i * 37L;
            _floatValues[i] = (i % 10_000) / 3f;
            _doubleValues[i] = (i % 10_000) / 7d;
            _stringValues[i] = $"val-{i % 2048}";
        }
    }

    [Benchmark(Baseline = true)]
    public Task WritePlankAsync()
    {
        _currentLibrary = "plank";
        return WriteWithPlankAsync();
    }

    [Benchmark]
    public void WriteParquetSharp()
    {
        _currentLibrary = "parquetsharp";
        WriteWithParquetSharp();
    }

    [Benchmark]
    public Task WriteParquetNetAsync()
    {
        _currentLibrary = "parquet.net";
        return WriteWithParquetNetAsync();
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        if (string.IsNullOrEmpty(_currentLibrary))
            throw new InvalidOperationException("Benchmark library key was not set before iteration cleanup.");
        var snapshot = ReadColumnBytes(_lastParquetBuffer);
        EncodingBenchmarkMetrics.Record(_currentLibrary, DataType, EncodingName, Rows, snapshot);
        _lastParquetBuffer = [];
    }

    async Task WriteWithPlankAsync()
    {
        var column = new PlankColumn(
            "value",
            DataType switch
            {
                "bool" => ParquetPhysicalType.Boolean,
                "int32" => ParquetPhysicalType.Int32,
                "int64" => ParquetPhysicalType.Int64,
                "float" => ParquetPhysicalType.Float,
                "double" => ParquetPhysicalType.Double,
                "string" => ParquetPhysicalType.ByteArray,
                _ => throw new InvalidOperationException($"Unknown type '{DataType}'.")
            },
            new ColumnOptions(ParquetRepetition.Required, [MapPlankEncoding(EncodingName)]));
        var schema = new PlankSchema([column]);
        await using var stream = new MemoryStream(capacity: Rows * 16);
        using var writer = Plank.Writing.ParquetWriter.Create(stream, schema, new ParquetWriterOptions
        {
            Compression = CompressionKind.None,
            ExpectedRowGroupCount = 1,
            RowGroupRowCountHint = checked((uint)Rows),
            DateTimeKindHandling = DateTimeKindHandling.PreserveClockTime
        });
        var rowGroup = writer.StartRowGroup();
        switch (DataType)
        {
            case "bool":
                await rowGroup.WriteAsync(column, _boolValues).ConfigureAwait(false);
                break;
            case "int32":
                await rowGroup.WriteAsync(column, _int32Values).ConfigureAwait(false);
                break;
            case "int64":
                await rowGroup.WriteAsync(column, _int64Values).ConfigureAwait(false);
                break;
            case "float":
                await rowGroup.WriteAsync(column, _floatValues).ConfigureAwait(false);
                break;
            case "double":
                await rowGroup.WriteAsync(column, _doubleValues).ConfigureAwait(false);
                break;
            case "string":
                await rowGroup.WriteAsync(column, _stringValues).ConfigureAwait(false);
                break;
            default:
                throw new InvalidOperationException($"Unknown type '{DataType}'.");
        }

        writer.CloseFile();
        await stream.FlushAsync().ConfigureAwait(false);
        _lastParquetBuffer = stream.ToArray();
    }

    void WriteWithParquetSharp()
    {
        using var stream = new MemoryStream(capacity: Rows * 16);
        using var writerProperties = BuildParquetSharpWriterProperties(EncodingName);
        using var writer = new ParquetFileWriter(stream, [BuildParquetSharpColumn(DataType)], null,
            writerProperties, null, true);
        using var rowGroupWriter = writer.AppendRowGroup();
        switch (DataType)
        {
            case "bool":
                using (var col = rowGroupWriter.NextColumn().LogicalWriter<bool>())
                    col.WriteBatch(_boolValues);
                break;
            case "int32":
                using (var col = rowGroupWriter.NextColumn().LogicalWriter<int>())
                    col.WriteBatch(_int32Values);
                break;
            case "int64":
                using (var col = rowGroupWriter.NextColumn().LogicalWriter<long>())
                    col.WriteBatch(_int64Values);
                break;
            case "float":
                using (var col = rowGroupWriter.NextColumn().LogicalWriter<float>())
                    col.WriteBatch(_floatValues);
                break;
            case "double":
                using (var col = rowGroupWriter.NextColumn().LogicalWriter<double>())
                    col.WriteBatch(_doubleValues);
                break;
            case "string":
                using (var col = rowGroupWriter.NextColumn().LogicalWriter<string>())
                    col.WriteBatch(_stringValues);
                break;
            default:
                throw new InvalidOperationException($"Unknown type '{DataType}'.");
        }

        writer.Close();
        stream.Flush();
        _lastParquetBuffer = stream.ToArray();
    }

    async Task WriteWithParquetNetAsync()
    {
        var options = new ParquetOptions
        {
            UseDictionaryEncoding = EncodingName == "dictionary",
            UseDeltaBinaryPackedEncoding = EncodingName == "delta_binary_packed"
        };
        await using var stream = new MemoryStream(capacity: Rows * 16);
        switch (DataType)
        {
            case "bool":
            {
                var field = new DataField<bool>("value");
                var schema = new ParquetSchema(field);
                await using var writer = await Parquet.ParquetWriter.CreateAsync(schema, stream, options, false)
                    .ConfigureAwait(false);
                writer.CompressionMethod = CompressionMethod.None;
                using var rowGroupWriter = writer.CreateRowGroup();
                await rowGroupWriter.WriteColumnAsync(new DataColumn(field, _boolValues)).ConfigureAwait(false);
                break;
            }
            case "int32":
            {
                var field = new DataField<int>("value");
                var schema = new ParquetSchema(field);
                await using var writer = await Parquet.ParquetWriter.CreateAsync(schema, stream, options, false)
                    .ConfigureAwait(false);
                writer.CompressionMethod = CompressionMethod.None;
                using var rowGroupWriter = writer.CreateRowGroup();
                await rowGroupWriter.WriteColumnAsync(new DataColumn(field, _int32Values)).ConfigureAwait(false);
                break;
            }
            case "int64":
            {
                var field = new DataField<long>("value");
                var schema = new ParquetSchema(field);
                await using var writer = await Parquet.ParquetWriter.CreateAsync(schema, stream, options, false)
                    .ConfigureAwait(false);
                writer.CompressionMethod = CompressionMethod.None;
                using var rowGroupWriter = writer.CreateRowGroup();
                await rowGroupWriter.WriteColumnAsync(new DataColumn(field, _int64Values)).ConfigureAwait(false);
                break;
            }
            case "float":
            {
                var field = new DataField<float>("value");
                var schema = new ParquetSchema(field);
                await using var writer = await Parquet.ParquetWriter.CreateAsync(schema, stream, options, false)
                    .ConfigureAwait(false);
                writer.CompressionMethod = CompressionMethod.None;
                using var rowGroupWriter = writer.CreateRowGroup();
                await rowGroupWriter.WriteColumnAsync(new DataColumn(field, _floatValues)).ConfigureAwait(false);
                break;
            }
            case "double":
            {
                var field = new DataField<double>("value");
                var schema = new ParquetSchema(field);
                await using var writer = await Parquet.ParquetWriter.CreateAsync(schema, stream, options, false)
                    .ConfigureAwait(false);
                writer.CompressionMethod = CompressionMethod.None;
                using var rowGroupWriter = writer.CreateRowGroup();
                await rowGroupWriter.WriteColumnAsync(new DataColumn(field, _doubleValues)).ConfigureAwait(false);
                break;
            }
            case "string":
            {
                var field = new DataField<string>("value");
                var schema = new ParquetSchema(field);
                await using var writer = await Parquet.ParquetWriter.CreateAsync(schema, stream, options, false)
                    .ConfigureAwait(false);
                writer.CompressionMethod = CompressionMethod.None;
                using var rowGroupWriter = writer.CreateRowGroup();
                await rowGroupWriter.WriteColumnAsync(new DataColumn(field, _stringValues)).ConfigureAwait(false);
                break;
            }
            default:
                throw new InvalidOperationException($"Unknown type '{DataType}'.");
        }

        await stream.FlushAsync().ConfigureAwait(false);
        _lastParquetBuffer = stream.ToArray();
    }

    static EncodingBenchmarkSizeSnapshot ReadColumnBytes(byte[] parquetData)
    {
        using var stream = new MemoryStream(parquetData, writable: false);
        using var reader = new ParquetFileReader(stream);
        using var rowGroup = reader.RowGroup(0);
        using var chunk = rowGroup.MetaData.GetColumnChunkMetaData(0);
        var fileBytes = parquetData.LongLength;
        return new EncodingBenchmarkSizeSnapshot(chunk.TotalCompressedSize, chunk.TotalUncompressedSize, fileBytes);
    }

    static ParquetSharp.Column BuildParquetSharpColumn(string dataType)
        => dataType switch
        {
            "bool" => new ParquetSharp.Column<bool>("value"),
            "int32" => new ParquetSharp.Column<int>("value"),
            "int64" => new ParquetSharp.Column<long>("value"),
            "float" => new ParquetSharp.Column<float>("value"),
            "double" => new ParquetSharp.Column<double>("value"),
            "string" => new ParquetSharp.Column<string>("value"),
            _ => throw new InvalidOperationException($"Unknown type '{dataType}'.")
        };

    static WriterProperties BuildParquetSharpWriterProperties(string encoding)
    {
        var builder = new WriterPropertiesBuilder().Compression(Compression.Uncompressed);
        if (encoding == "dictionary")
            return builder.EnableDictionary().Build();

        var mapped = encoding switch
        {
            "plain" => Encoding.Plain,
            "delta_binary_packed" => Encoding.DeltaBinaryPacked,
            "delta_length_byte_array" => Encoding.DeltaLengthByteArray,
            "delta_byte_array" => Encoding.DeltaByteArray,
            "byte_stream_split" => Encoding.ByteStreamSplit,
            _ => throw new InvalidOperationException($"Unknown encoding '{encoding}'.")
        };
        return builder.DisableDictionary().Encoding(mapped).Build();
    }

    static EncodingKind MapPlankEncoding(string encoding)
        => encoding switch
        {
            "plain" => EncodingKind.Plain,
            "dictionary" => EncodingKind.PlainDictionary,
            "delta_binary_packed" => EncodingKind.DeltaBinaryPacked,
            "delta_length_byte_array" => EncodingKind.DeltaLengthByteArray,
            "delta_byte_array" => EncodingKind.DeltaByteArray,
            "byte_stream_split" => EncodingKind.ByteStreamSplit,
            _ => throw new InvalidOperationException($"Unknown encoding '{encoding}'.")
        };

}
